//
//  BuildXLSandbox.cpp
//  BuildXLSandbox
//
//  Copyright © 2018 Microsoft. All rights reserved.
//

#include "BuildXLSandbox.hpp"
#include "VNodeHandler.hpp"
#include "FileOpHandler.hpp"
#include "Listeners.hpp"

#define super IOService

OSDefineMetaClassAndStructors(BuildXLSandbox, IOService)

bool BuildXLSandbox::init(OSDictionary *dictionary)
{
    if (!super::init(dictionary))
    {
        return false;
    }

    lock_ = IORecursiveLockAlloc();
    if (!lock_)
    {
        return false;
    }

    reportQueueSize_ = kSharedDataQueueSizeDefault;
    reportQueues_ = ConcurrentMultiplexingQueue::Create();
    if (!reportQueues_)
    {
        return false;
    }

    trackedProcesses_ = ConcurrentDictionary::withCapacity(kProcessDictionaryCapacity, "TrackedProcesses");
    if (!trackedProcesses_)
    {
        return false;
    }

    kern_return_t result = InitializeListeners();
    if (result != KERN_SUCCESS)
    {
        return false;
    }

    return true;
}

void BuildXLSandbox::free(void)
{
    UninitializeListeners();

    if (lock_)
    {
        IORecursiveLockFree(lock_);
        lock_ = nullptr;
    }

    OSSafeReleaseNULL(trackedProcesses_);
    OSSafeReleaseNULL(reportQueues_);

    super::free();
}

bool BuildXLSandbox::start(IOService *provider)
{
    bool success = super::start(provider);
    if (success)
    {
        registerService();
    }

    return success;
}

void BuildXLSandbox::stop(IOService *provider)
{
    super::stop(provider);
}

void BuildXLSandbox::InitializePolicyStructures()
{
    policyHandle_ = {0};

    Listeners::g_dispatcher = this;

    buildxlPolicyOps_ =
    {
        // NOTE: handle preflight instead of mpo_vnode_check_lookup because trying to get the path for a vnode
        //       (vn_getpath) inside of that handler overwhelms the system very quickly
        .mpo_vnode_check_lookup_preflight = Listeners::mpo_vnode_check_lookup_pre,

        // this event happens right after fork only on the child processes
        .mpo_cred_label_associate_fork    = Listeners::mpo_cred_label_associate_fork,

        // some tools spawn child processes using execve() and vfork(), while this is non standard, we have to handle it
        // especially for shells like csh / tcsh
        .mpo_cred_label_update_execve     = Listeners::mpo_cred_label_update_execve,

        .mpo_vnode_check_exec             = Listeners::mpo_vnode_check_exec,

        .mpo_proc_notify_exit             = Listeners::mpo_proc_notify_exit,

        .mpo_vnode_check_create           = Listeners::mpo_vnode_check_create,

        .mpo_vnode_check_readlink         = Listeners::mpo_vnode_check_readlink,
    };

    policyConfiguration_ =
    {
        .mpc_name            = kBuildXLSandboxClassName,
        .mpc_fullname        = "Sandbox for process liftetime, I/O observation and control",
        .mpc_labelnames      = NULL,
        .mpc_labelname_count = 0,
        .mpc_ops             = &buildxlPolicyOps_,
        .mpc_loadtime_flags  = MPC_LOADTIME_FLAG_UNLOADOK,
        .mpc_field_off       = NULL,
        .mpc_runtime_flags   = 0,
        .mpc_list            = NULL,
        .mpc_data            = NULL
    };
}

kern_return_t BuildXLSandbox::InitializeListeners()
{
    InitializePolicyStructures();
    kern_return_t status = mac_policy_register(&policyConfiguration_, &policyHandle_, NULL);
    if (status != KERN_SUCCESS)
    {
        log_error("Registering TrustedBSD MAC policy callbacks failed with error code %#X", status);
        return status;
    }

    buildxlVnodeListener_ = kauth_listen_scope(KAUTH_SCOPE_VNODE, Listeners::buildxl_vnode_listener, reinterpret_cast<void *>(this));
    if (buildxlVnodeListener_ == nullptr)
    {
        log_error("%s", "Registering callback for KAUTH_SCOPE_VNODE scope failed!");
        return KERN_FAILURE;
    }

    buildxlFileOpListener_ = kauth_listen_scope(KAUTH_SCOPE_FILEOP, Listeners::buildxl_file_op_listener, reinterpret_cast<void *>(this));
    if (buildxlFileOpListener_ == nullptr)
    {
        log_error("%s", "Registering callback for KAUTH_SCOPE_FILEOP scope failed!");
        return KERN_FAILURE;
    }

    return KERN_SUCCESS;
}

void BuildXLSandbox::UninitializeListeners()
{
    if (buildxlVnodeListener_ != nullptr)
    {
        kauth_unlisten_scope(buildxlVnodeListener_);
        log_debug("%s", "Deregistered callback for KAUTH_SCOPE_VNODE scope");
        buildxlVnodeListener_ = nullptr;
    }

    if (buildxlFileOpListener_ != nullptr)
    {
        kauth_unlisten_scope(buildxlFileOpListener_);
        log_debug("%s", "Deregistered callback for KAUTH_SCOPE_FILEOP scope");
        buildxlFileOpListener_ = nullptr;
    }

    mac_policy_unregister(policyHandle_);
    log_debug("%s", "Deregistered TrustedBSD MAC policy callbacks");
}

void BuildXLSandbox::SetReportQueueSize(UInt32 reportQueueSize)
{
    reportQueueSize_ = (reportQueueSize == 0 || reportQueueSize > kSharedDataQueueSizeMax) ? kSharedDataQueueSizeDefault : reportQueueSize;
    log_debug("Size set to: %u", reportQueueSize_);
}

UInt32 BuildXLSandbox::GetReportQueueEntryCount()
{
    return (reportQueueSize_ * 1024 * 1024) / sizeof(AccessReport);
}

IOReturn BuildXLSandbox::AllocateReportQueueForClientProcess(pid_t pid)
{
    EnterMonitor

    const OSSymbol *key = ProcessObject::computePidHashCode(pid);
    auto *queue = ConcurrentSharedDataQueue::withEntries(GetReportQueueEntryCount(), sizeof(AccessReport));
    bool success = reportQueues_->insertQueue(key, queue);
    OSSafeReleaseNULL(queue);
    OSSafeReleaseNULL(key);

    return success ? kIOReturnSuccess : kIOReturnError;
}

typedef struct {
    int ctxPid;
    BuildXLSandbox *sandbox;
} ReleaseContext;

IOReturn BuildXLSandbox::FreeReportQueuesForClientProcess(pid_t pid)
{
    EnterMonitor

    const OSSymbol *key = ProcessObject::computePidHashCode(pid);
    bool success = reportQueues_->removeQueues(key);
    OSSafeReleaseNULL(key);

    log_debug("Freeing report queues %s for client PID(%d), remaining report queue mappings in wired memory: %d", (success ? "succeeded" : "failed"),
              pid, reportQueues_->getBucketCount());

    // Make sure to also cleanup any remaining tracked process objects as the client could have exited abnormally (crashed)
    // and we don't want those objects to stay around any longer

    ReleaseContext ctx = {
        .ctxPid = pid,
        .sandbox = this
    };

    trackedProcesses_->forEach(&ctx, [](void *data, const OSSymbol *key, const OSObject *value)
    {
        ProcessObject *process = OSDynamicCast(ProcessObject, value);
        if (process != nullptr && data != nullptr)
        {
            ReleaseContext *context = (ReleaseContext *)data;
            if (context != nullptr && process->getClientPid() == context->ctxPid)
            {
                log_debug("Released tracked process PID(%d) for client process PID(%d) on cleanup", process->getProcessId(), process->getClientPid());
                context->sandbox->trackedProcesses_->removeProcess(context->ctxPid);
            }
        }
    });

    return success ? kIOReturnSuccess : kIOReturnError;
}

IOReturn BuildXLSandbox::SetReportQueueNotificationPort(mach_port_t port, pid_t pid)
{
    EnterMonitor

    const OSSymbol *key = ProcessObject::computePidHashCode(pid);
    bool success = reportQueues_->setNotifactonPortForNextQueue(key, port);
    OSSafeReleaseNULL(key);

    return success ? kIOReturnSuccess : kIOReturnError;
}

IOMemoryDescriptor* const BuildXLSandbox::GetReportQueueMemoryDescriptor(pid_t pid)
{
    EnterMonitor

    const OSSymbol *key = ProcessObject::computePidHashCode(pid);
    IOMemoryDescriptor* descriptor = reportQueues_->getMemoryDescriptorForNextQueue(key);
    OSSafeReleaseNULL(key);

    return descriptor;
}

bool const BuildXLSandbox::SendFileAccessReport(pid_t clientPid, AccessReport &report, bool roundRobin)
{
    EnterMonitor

    const OSSymbol *key = ProcessObject::computePidHashCode(clientPid);
    bool success = reportQueues_->enqueueData(key, &report, sizeof(report), roundRobin);
    OSSafeReleaseNULL(key);

    log_error_or_debug(verboseLoggingEnabled, !success,
                       "BuildXLSandbox::SendFileAccessReport ClientPID(%d), PID(%d), Root PID(%d), PIP(%#llX), Operation: %s, Path: %s, Status: %d, Sent: %s",
                       clientPid, report.pid, report.rootPid, report.pipId, report.operation, report.path, report.status, success ? "succeeded" : "failed");

    return success;
}

ProcessObject* BuildXLSandbox::FindTrackedProcess(pid_t pid)
{
    // NOTE: this has to be very fast when we are not tracking any processes (i.e., trackedProcesses_ is empty)
    //       because this is called on every single file access any process makes
    return trackedProcesses_->getProcess(pid);
}

bool BuildXLSandbox::TrackRootProcess(const ProcessObject *process)
{
    EnterMonitor

    pid_t pid = process->getProcessId();

    // if mapping for 'pid' exists --> remove it (this can happen only if clients are nested, e.g., BuildXL runs BuildXL)
    ProcessObject *existingProcess = trackedProcesses_->getProcess(pid);
    if (existingProcess)
    {
        int oldTreeCount = existingProcess->getProcessTreeCount();
        UntrackProcess(pid, existingProcess);
        log_verbose(verboseLoggingEnabled, "Untracking process PID = %d early, parent PID = %d, tree size (old/new) = %d/%d",
                    pid, existingProcess->getProcessId(), oldTreeCount, existingProcess->getProcessTreeCount());
    }

    bool inserted = trackedProcesses_->insertProcess(process);
    log_verbose(verboseLoggingEnabled, "Tracking top process PID = %d; inserted: %d", pid, inserted);
    return inserted;
}

bool BuildXLSandbox::TrackChildProcess(pid_t childPid, ProcessObject *rootProcess)
{
    EnterMonitor

    ProcessObject *existingProcess = trackedProcesses_->getProcess(childPid);
    if (existingProcess)
    {
        log_debug("Child process PID(%d) already tracked; existing: Root PID(%d), intended new: Root PID(%d)",
                  childPid, existingProcess->getProcessId(), rootProcess->getProcessId());

        if (existingProcess->getPipId() != rootProcess->getPipId() &&
            existingProcess->getClientPid() != rootProcess->getClientPid())
        {
            log_error("Found existing child process (PipId: %#llX / ClientId: %d) that does not match its root process data (PipId: %#llX / ClientId: %d)",
                      existingProcess->getPipId(), existingProcess->getClientPid(), rootProcess->getPipId(), rootProcess->getClientPid());
        }

        return false;
    }

    const OSSymbol *childPidKey = ProcessObject::computePidHashCode(childPid);

    // add the child process to process tree
    trackedProcesses_->insert(childPidKey, rootProcess);
    rootProcess->incrementProcessTreeCount();
    log_verbose(verboseLoggingEnabled, "Tracking child process PID = %d; parent: %d (tree size = %d)",
                childPid, rootProcess->getProcessId(), rootProcess->getProcessTreeCount());

    OSSafeReleaseNULL(childPidKey);
    return true;
}

bool BuildXLSandbox::UntrackProcess(pid_t pid, pipid_t expectedPipId)
{
    EnterMonitor

    ProcessObject *process = FindTrackedProcess(pid);
    if (process && (expectedPipId == -1 || process->getPipId() == expectedPipId))
    {
        UntrackProcess(pid, process);
        return true;
    }
    else
    {
        return false;
    }
}

void BuildXLSandbox::UntrackProcess(pid_t pid, ProcessObject *process)
{
    EnterMonitor

    process->retain();
    do
    {
        log_verbose(verboseLoggingEnabled, "Untracking entry %d --> %d (PipId: %#llX, process tree count: %d)",
                    pid, process->getProcessId(), process->getPipId(), process->getProcessTreeCount());

        // remove the mapping for 'pid'
        if (!trackedProcesses_->removeProcess(pid))
        {
            log_error("Process with PID = %d not found in tracked processes", pid);
            break;
        }

        // decrement tree count for the given process
        process->decrementProcessTreeCount();

        // if the process tree is empty, report to clients that the process and all its children exited
        if (process->hasEmptyProcessTree())
        {
            AccessHandler accessHandler = AccessHandler(process, this);
            accessHandler.ReportProcessTreeCompleted();
        }
    } while (0);
    process->release();
}

#undef super
