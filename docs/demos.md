# BuildXL demos
Some demos are included under /Public/Src/Demos to showcase the main features of the BuildXL sandbox. These demos are not intended to be a comprehensive walkthrough, but just a simple set of examples that can be used as a starting point to learn how the sandbox can be configured and used.

The BuildXL sandbox is capable of running an arbitrary process and 'detour' all its OS calls. As a result, it is possible to obtain very rich information about what the process is doing and even block some of the process calls. In order to do this, the sandbox needs to be configured before a process is run. This configuration will instruct the sandbox about what to monitor and block.

The sandbox is configured through a *manifest*. The manifest contains high-level information (for example, if child processes should be monitored or not) but also more fine-grained information related to how to treat the running process file accesses. As part of the manifest, *scopes* can be defined (i.e. a directory and all its recursive content), where each scope can have its own policy. A policy includes what operations are allowed for a given scope (e.g. the process can be allowed to read and write under c:\foo, but only read under c:\foo\readonly. A policy also includes if file accesses should be reported back, and if a file access is expected or not. Blocking an access is also a capability that can be specified via a policy.

These demos showcase three main features of the sandbox: reporting file accesses, blocking accesses and retrieving the process tree.

## Using the sandbox to report file accesses (Public/Src/Demos/ReportAccesses)

This demo is able to run an arbitrary process and report back all the file accesses that the process (and its child processes) made. For example, one can run:

```
E:\temp>dotnet <repo_root>\bin\[Debug|Release]\ReportAccesses.dll notepad myFile.txt
```

This will actually open notepad.exe and myFile.txt will be created. After exiting notepad, the tool reports:

```
Process 'notepad' ran under BuildXL sandbox with arguments 'myFile.txt' and returned with exit code '0'. Sandbox reports 27 file accesses:
C:\WINDOWS\SYSTEM32\notepad.exe
C:\Windows\Fonts\staticcache.dat
C:\WINDOWS\Registration\R00000000000d.clb
C:\WINDOWS\Registration
C:\WINDOWS\Registration\R000000000001.clb
C:\WINDOWS\Globalization\Sorting\sortdefault.nls
C:\WINDOWS\SYSTEM32\OLEACCRC.DLL
E:\temp
E:\temp\myFile.txt
C:\WINDOWS\SYSTEM32\imageres.dll
C:\WINDOWS\SysWOW64\propsys.dll
C:\WINDOWS\system32\propsys.dll
C:\ProgramData\Microsoft\Windows\Caches
C:\ProgramData\Microsoft\Windows\Caches\cversions.2.db
C:\Users\username\AppData\Local\Microsoft\Windows\Caches
C:\Users\username\AppData\Local\Microsoft\Windows\Caches\cversions.3.db
C:\ProgramData\Microsoft\Windows\Caches\{DDF571F2-BE98-426D-8288-1A9A39C3FDA2}.2.ver0x0000000000000000.db
```

Let's take a closer look at the code to understand how this happened. A sandbox is configured via `SandboxedProcessInfo`. The main information to be provided here is 1) what process to run 2) the arguments to be passed and 3) the manifest to be used to configure the sandbox:

```cs
// Public/Src/Demos/ReportAccesses/FileAccessReporter.cs
var info =
    new SandboxedProcessInfo(
        PathTable,
        new SimpleSandboxedProcessFileStorage(workingDirectory), 
        pathToProcess,
        CreateManifestToAllowAllAccesses(PathTable),
        disableConHostSharing: true,
        loggingContext: m_loggingContext)
    {
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        PipSemiStableHash = 0,
        PipDescription = "Simple sandbox demo",
        SandboxedKextConnection = OperatingSystemHelper.IsUnixOS ? new SandboxedKextConnection(numberOfKextConnections: 2) : null
    };
``` 

`CreateManifestToAllowAllAccesses` is where the most interesting things happen:

```cs
var fileAccessManifest = new FileAccessManifest(pathTable)
    {
        FailUnexpectedFileAccesses = false,
        ReportFileAccesses = true,
        MonitorChildProcesses = true,
    };
```            
We are creating a manifest that configures the sandbox so:
* No file accesses are blocked (`FailUnexpectedFileAccesses = false`)
* All files accesses are reported (`ReportFileAccesses = true`)
* Child processes are also monitored (`MonitorChildProcesses = true`)

As a result of this configuration, all file accesses are allowed and reported. Each file access carries structured information that includes the type of operation, disposition, attributes, etc. In this simple demo we are just printing out the path of each access.

## Blocking accesses (Public/Src/Demos/BlockAccesses)

The next demo shows how to use BuildXL sandbox to actually block accesses with certain characteristics. Given a directory provided by the user, a process is launched under the sandbox which tries to enumerate the given directory recursively and perform a read on every file found. However, a collection of directories to block can also be provided: the sandbox will make sure that any access that falls under these directories will be blocked, preventing the tool from accessing those files.

Consider the following directory structure:

```
E:\TEST
├───bin
│       t1.exe
│
├───obj
│       t1.obj
│
└───source
        t1.txt
```        

And let's see what happens if we run:

```dotnet <repo_root>\bin\[Debug|Release]\BlockAccesses.dll e:\test e:\test\bin e:\test\obj```

Here we are trying to enumerate ``e:\test`` recursively, but block any access under ``e:\test\obj`` and ``e:\test\bin``. The result is:

```
Enumerated the directory 'e:\test'. The following accesses were reported:
Allowed -> [Read] C:\WINDOWS\system32\cmd.exe
Allowed -> [Probe] e:\test
Allowed -> [Probe] e:
Allowed -> [Enumerate] e:\test
Allowed -> [Enumerate] e:\test\..
Allowed -> [Enumerate] e:\test\bin
Allowed -> [Enumerate] e:\test\obj
Allowed -> [Enumerate] e:\test\source
Allowed -> [Enumerate] e:\test\bin\..
Allowed -> [Enumerate] e:\test\bin\t1.exe
Allowed -> [Probe] e:\test\bin
Denied -> [Probe] e:\test\bin\t1.exe
Allowed -> [Enumerate] e:\test\obj\..
Allowed -> [Enumerate] e:\test\obj\src2.txt
Allowed -> [Enumerate] e:\test\obj\t1.obj
Allowed -> [Probe] e:\test\obj
Denied -> [Probe] e:\test\obj\t1.obj
Allowed -> [Enumerate] e:\test\source\.
Allowed -> [Enumerate] e:\test\source\t1.txt
Allowed -> [Probe] e:\test\source
Allowed -> [Probe] e:\test\source\t1.txt
Allowed -> [Read] e:\test\source\t1.txt
```

Each access is reported, and for each case, we are printing out the type of access: a read, a check for existence (a probe) or an enumeration. Additionally, if the access was allowed or denied. Observe that since we are only blocking accesses under obj and bin, only these two accesses were blocked:

```
Denied -> [Probe] e:\test\obj\t1.obj
Denied -> [Probe] e:\test\bin\t1.exe
```
And given that the probe was blocked, there was not even an attempt to read from those files, since they failed at enumeration time to begin with.

Let's jump now into more details to understand how this was achieved. If we look at how the manifest was constructed, we can see the following:

```cs 
// Public/Src/Demos/BlockAccesses/BlockingEnumerator.cs

// We allow all file accesses at the root level, so by default everything is allowed
fileAccessManifest.AddScope(AbsolutePath.Invalid, FileAccessPolicy.MaskNothing, FileAccessPolicy.AllowAll);

// We block access on all provided directories
foreach (var directoryToBlock in directoriesToBlock)
{
    fileAccessManifest.AddScope(
        directoryToBlock,
        FileAccessPolicy.MaskAll,
        FileAccessPolicy.Deny & FileAccessPolicy.ReportAccess);
}
```

The first line is setting the policy for the *global* (or root) scope. You can think of that as the default policy for an arbitrary access. This policy is just allowing any access to happen. Then, we iterate over the directories that have to be blocked, and for each one, we add a *scope*. A scope can be thought of as a directory with all its recursive content. Scopes can be nested, and the most specific scope is the one that defines the policy for an access that falls under it.

In this case, we are configuring each scope so all accesses are blocked (```FileAccessPolicy.Deny```) but also to report the access (```FileAccessPolicy.ReportAccess```). Additionally, we have to decide what to do with the policy we are inheriting from the parent scope. Here one can decide to mask some of the parent configuration. In this case, we are just masking all the properties from the parent, to make sure nothing is allowed.

Finally, we are retrieving all the accesses by inspecting the result of the sandbox:

```cs
// Public/Src/Demos/BlockAccesses/Program.cs
SandboxedProcessResult result = sandboxDemo.EnumerateWithBlockedDirectories(directoryToEnumerate, directoriesToBlock).GetAwaiter().GetResult();

var allAccesses = result
    .FileAccesses
    .Select(access => $"{(access.Status == FileAccessStatus.Denied ? "Denied" : "Allowed")} -> {RequestedAccessToString(access.RequestedAccess)} {access.GetPath(pathTable)}")
    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
```

``SandboxedProcessResult.FileAccesses`` contains all the reported accesses. So we just iterate over them and print some of the details.

## Retrieving the process tree
The last demo shows how the sandbox can be used to retrieve the process tree of a process that was run under the sandbox. The process tree contains all the historical information. This is, any child process that was created during the execution of the main process is reported, together with structured information that contains IO and CPU counters, elapsed times, etc.

For example, let's run a git fetch on an arbitrary repo:

```dotnet <repo_root>\bin\[Debug|Release]\ProcessTree.dll git fetch```

The result is:

```
Process 'git' ran under the sandbox. The process tree is the following:
C:\Program Files\Git\cmd\git.exe [ran 4369.9082ms]
└──C:\Program Files\Git\mingw64\bin\git.exe [ran 4205.3507ms]
   ├──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 3954.624ms]
      └──C:\Program Files\Git\mingw64\libexec\git-core\git-remote-https.exe [ran 3253.4994ms]
         └──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 1338.9764ms]
            └──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 1004.7528ms]
   ├──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 98.0952ms]
   ├──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 132.415ms]
   └──C:\Program Files\Git\mingw64\libexec\git-core\git.exe [ran 94.806ms]
```

The demo is printing out the process tree, including the elapsed running time for each process.

Let's jump into the code. The manifest creation for this demo is not super interesting, the only relevant part being setting a specific flag to log the data of all processes:

```cs
// Public/Src/Demos/ProcessTree/ProcessTreeBuilder.cs
 var fileAccessManifest = new FileAccessManifest(pathTable)
{
    ...
    // Let's turn on process data collection, so we can report a richer tree
    LogProcessData = true
};
```

The list of processes are reported as part of the sandbox result. So in this case we are just creating a tree, for nicer visualization, and printing out some of the reported information that is associated to each process:

```cs
// Public/Src/Demos/ProcessTree/ProcessTreeBuilder.cs
SandboxedProcessResult result = RunProcessUnderSandbox(pathToProcess, arguments);
// The sandbox reports all processes as a list. Let's make them a tree for better visualization.
return ComputeTree(result.Processes);
```
All the processes (main and children) are reported in ``SandboxedProcessResult.Processes`` as a list of processes. After the tree is constructed, we decided to print the path of the process executable, and the running time:

```cs
/// Public/Src/Demos/ProcessTree/Program.cs
Console.WriteLine($"{indent}{reportedProcess.Path} [ran {(reportedProcess.ExitTime - reportedProcess.CreationTime).TotalMilliseconds}ms]");
```