// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#ifndef SandboxedPip_hpp
#define SandboxedPip_hpp

#include <IOKit/IOLib.h>
#include <IOKit/IOService.h>
#include <IOKit/IOSharedDataQueue.h>
#include <sys/proc.h>
#include <sys/vnode.h>

#include "BuildXLSandboxShared.hpp"
#include "CacheRecord.hpp"
#include "FileAccessManifestParser.hpp"
#include "IOBuffer.hpp"
#include "PolicyResult.h"
#include "ThreadLocal.hpp"
#include "Trie.hpp"

/*!
 * Represents the root of the process tree being tracked.
 *
 * The 'Pip' name comes from the BuildXL terminology, where 'pip' is a generic build task
 * that may spawn arbitrary number of child processes.
 *
 * Every pip comes with a 'FileAccessManifest' (FAM).  A FAM contains all the policies relevant
 * for sandboxing a pip, e.g., which file accesses are permitted, which are not, which should
 * be reported back, etc.
 */
class SandboxedPip : public OSObject
{
    OSDeclareDefaultStructors(SandboxedPip);

private:

    /*! Process id of the client tracking this process. */
    pid_t clientPid_;

    /*! Process id of the root process of this pip. */
    pid_t processId_;

    /*! File access manifest payload bytes */
    IOBuffer *payload_;

    /*! File access manifest (contains pointers into the 'payload_' byte array */
    FileAccessManifestParseResult fam_;

    /*! Number of processses in this pip's process tree */
    int processTreeCount_;

    /*! Maps every accessed path to a 'CacheRecord' object (which contains caching information regarding that path) */
    Trie *pathCache_;

    /*! A thread-local storage for remembering the last looked up path by every thread. */
    ThreadLocal *lastPathLookup_;

    /*! Various counters.  IMPORTANT: counters may be globally disabled so no logic may rely on their values. */
    AllCounters counters_;

    static OSObject* CacheRecordFactory(void *)
    {
        return CacheRecord::create();
    };

    bool init(pid_t clientPid, pid_t processPid, IOBuffer *payload);

protected:

    void free() override;

public:

    /*! Process id of the client tracking this process. */
    pid_t getClientPid() const { return clientPid_; }

    /*! Process id of the root process of this pip. */
    pid_t getProcessId() const { return processId_; }

    /*! A unique identifier of this pip. */
    pipid_t getPipId() const   { return fam_.GetPipId()->PipId; }

    /*! File access manifest record for this pip (to be used for checking file accesses) */
    PCManifestRecord getManifestRecord() const    { return fam_.GetUnixRootNode(); }

    /*! File access manifest flags */
    FileAccessManifestFlag getFamFlags() const    { return fam_.GetFamFlags(); }

    /*!
     * Returns the full path of the root process of this pip.
     * The lenght of the path is stored in the 'length' argument because the path is not necessarily 0-terminated.
     */
    const char* getProcessPath(int *length) const { return fam_.GetProcessPath(length); }

    /*! Various counters. */
    AllCounters* Counters() { return &counters_; }

    /*!
     * Uses a thread-local storage to save a given path as the last path that was looked up on the current thread.
     */
    void setLastLookedUpPath(const char *path)
    {
        const OSSymbol *pathSym = OSSymbol::withCString(path);
        lastPathLookup_->insert(pathSym);
        OSSafeReleaseNULL(pathSym);
    }

    /*!
     * Returns the last path saved by the current thread by calling the 'setLastLookedUpPath' method.
     *
     * (In practice, this is the path associated with the last MAC_LOOKUP event that happened on the current thread).
     */
    const char* getLastLookedUpPath()
    {
        OSSymbol *value = OSDynamicCast(OSSymbol, lastPathLookup_->get());
        return value != nullptr ? value->getCStringNoCopy() : nullptr;
    }

    /*! Information about this pip that can be queried from user space */
    PipInfo introspect() const;

#pragma mark Process Tree Tracking

    /*! Number of currently active processes in this pip's process tree */
    int getTreeSize() const          { return processTreeCount_; }

    /*! Atomically increments this pip's process tree size and returns the size before increment. */
    int incrementProcessTreeCount() { return OSIncrementAtomic(&processTreeCount_); }

    /*! Atomically dencrements this pip's process tree size and returns the size before decrement. */
    int decrementProcessTreeCount() { return OSDecrementAtomic(&processTreeCount_); }

#pragma mark Report Caching

    /*!
     * Looks up a 'CacheRecord' associated with a given path.
     * If no such record exists, a new one is created and associated with the path.
     * Return value of NULL indicates that there is an inherent reason why the path cannot be added to cache.
     */
    inline CacheRecord* cacheLookup(const char *path)
    {
        OSObject *value = pathCache_->getOrAdd(path, nullptr, CacheRecordFactory);
        return OSDynamicCast(CacheRecord, value);
    }

#pragma mark Static Methods

    /*! Factory method. The caller is responsible for releasing the returned object. */
    static SandboxedPip* create(pid_t clientPid, pid_t processPid, IOBuffer *payload);
};

#endif /* SandboxedPip_hpp */
