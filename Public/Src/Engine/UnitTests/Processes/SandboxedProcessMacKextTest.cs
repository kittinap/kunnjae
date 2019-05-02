// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Interop.MacOS;
using BuildXL.Processes;
using BuildXL.Utilities;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using static BuildXL.Interop.MacOS.Sandbox.AccessReport;

#pragma warning disable AsyncFixer02

namespace Test.BuildXL.Processes
{
    public class SandboxedProcessMacKextTest : SandboxedProcessTestBase
    {
        public SandboxedProcessMacKextTest(ITestOutputHelper output)
            : base(output) { }

        private class KextConnection : IKextConnection
        {
            public ulong MinReportQueueEnqueueTime { get; set; }

            public TimeSpan CurrentDrought
            {
                get
                {
                    ulong nowNs = Sandbox.GetMachAbsoluteTime();
                    ulong minReportTimeNs = MinReportQueueEnqueueTime;
                    return TimeSpan.FromTicks(nowNs > minReportTimeNs ? (long)((nowNs - minReportTimeNs) / 100) : 0);
                }
            }

            public void Dispose() { }

            public bool IsInTestMode => true;

            public bool MeasureCpuTimes => true;

            public bool NotifyUsage(uint cpuUsage, uint availableRamMB) { return true; }

            public bool NotifyKextPipStarted(FileAccessManifest fam, SandboxedProcessMacKext process) { return true; }

            public void NotifyKextPipProcessTerminated(long pipId, int processId) { }

            public bool NotifyKextProcessFinished(long pipId, SandboxedProcessMacKext process) { return true; }

            public void ReleaseResources() { }
        }

        private readonly KextConnection s_connection = new KextConnection();

        private class ReportInstruction
        {
            public SandboxedProcessMacKext Process;
            public Sandbox.AccessReportStatistics Stats;
            public int Pid;
            public FileOperation Operation;
            public string Path;
            public bool Allowed;
        }

        [FactIfSupported(requiresUnixBasedOperatingSystem: true)]
        public async Task CheckProcessTreeTimoutOnReportQueueStarvationAsync()
        {
            var processInfo = CreateProcessInfoWithKextConnection(Operation.Echo("hi"));

            // Set the last enqueue time to now
            s_connection.MinReportQueueEnqueueTime = Sandbox.GetMachAbsoluteTime();

            var process = CreateAndStartSandboxedProcess(processInfo);
            var taskCancelationSource = new CancellationTokenSource();

            // Post nothing to the report queue, and the process tree must be timed out after ReportQueueProcessTimeout
            // has been reached.
            ContinouslyPostAccessReports(process, taskCancelationSource.Token);
            var result = await process.GetResultAsync();

            taskCancelationSource.Cancel();

            XAssert.IsTrue(result.Killed, "Expected process to have been killed");
            XAssert.IsFalse(result.TimedOut, "Didn't expect process to have timed out");
        }

        [FactIfSupported(requiresUnixBasedOperatingSystem: true)]
        public async Task CheckProcessTreeTimoutOnReportQueueStarvationAndStuckRootProcessAsync()
        {
            var processInfo = CreateProcessInfoWithKextConnection(Operation.Block());

            // Set the last enqueue time to now
            s_connection.MinReportQueueEnqueueTime = Sandbox.GetMachAbsoluteTime();

            // NOTE: Not wrapping this process in 'time' because KillAsync can only kill 'time' and the its child process (from 'Operation.Block').
            //       This is only because we are using a mock KextConnection here; in production, a real kext connection 
            //       notifies the kext which kills the surviving processes.
            var process = CreateAndStartSandboxedProcess(processInfo, measureTime: false);
            var taskCancelationSource = new CancellationTokenSource();

            // Post nothing to the report queue, and the process tree must be timed out after ReportQueueProcessTimeout
            // has been reached, including the stuck root process
            ContinouslyPostAccessReports(process, taskCancelationSource.Token);
            var result = await process.GetResultAsync();

            taskCancelationSource.Cancel();

            XAssert.IsTrue(result.Killed, "Expected process to have been killed");
            XAssert.IsFalse(result.TimedOut, "Didn't expect process to have timed out");
        }

        [FactIfSupported(requiresUnixBasedOperatingSystem: true)]
        public async Task CheckProcessTreeTimoutOnNestedChildProcessTimeoutWhenRootProcessExitedAsync()
        {
            var processInfo = CreateProcessInfoWithKextConnection(Operation.Echo("hi"));
            processInfo.NestedProcessTerminationTimeout = TimeSpan.FromMilliseconds(100);

            // Set the last enqueue time to now
            s_connection.MinReportQueueEnqueueTime = Sandbox.GetMachAbsoluteTime();

            var process = CreateAndStartSandboxedProcess(processInfo);
            var taskCancelationSource = new CancellationTokenSource();

            var time = Sandbox.GetMachAbsoluteTime();

            var childProcessPath = "/dummy/exe2";
            var instructions = new List<ReportInstruction>()
            {
                new ReportInstruction() {
                    Process = process,
                    Operation = FileOperation.OpProcessStart,
                    Stats = new Sandbox.AccessReportStatistics()
                    {
                        EnqueueTime = time + ((ulong) TimeSpan.FromSeconds(1).Ticks * 100),
                        DequeueTime = time + ((ulong) TimeSpan.FromSeconds(2).Ticks * 100),
                    },
                    Pid = 1235,
                    Path = childProcessPath,
                    Allowed = true
                },
                new ReportInstruction() {
                    Process = process,
                    Operation = FileOperation.OpProcessExit,
                    Stats = new Sandbox.AccessReportStatistics()
                    {
                        EnqueueTime = time + ((ulong) TimeSpan.FromSeconds(3).Ticks * 100),
                        DequeueTime = time + ((ulong) TimeSpan.FromSeconds(4).Ticks * 100),
                    },
                    Pid = process.ProcessId,
                    Path = "/dummy/exe",
                    Allowed = true
                },
                new ReportInstruction() {
                    Process = process,
                    Operation = FileOperation.OpKAuthCreateDir,
                    Stats = new Sandbox.AccessReportStatistics()
                    {
                        EnqueueTime = time + ((ulong) TimeSpan.FromSeconds(5).Ticks * 100),
                        DequeueTime = time + ((ulong) TimeSpan.FromSeconds(6).Ticks * 100),
                    },
                    Pid = 1235,
                    Path = childProcessPath,
                    Allowed = true
                },
            };

            ContinouslyPostAccessReports(process, taskCancelationSource.Token, instructions);
            var result = await process.GetResultAsync();
            taskCancelationSource.Cancel();

            XAssert.IsTrue(result.Killed, "Expected process to have been killed");
            XAssert.IsFalse(result.TimedOut, "Didn't expect process to have timed out");
            XAssert.IsNotNull(result.SurvivingChildProcesses, "Expected surviving child processes");
            XAssert.IsTrue(result.SurvivingChildProcesses.Any(p => p.Path == childProcessPath),
                $"Expected surviving child processes to contain {childProcessPath}; " +
                $"instead it contains: {string.Join(", ", result.SurvivingChildProcesses.Select(p => p.Path))}");
        }

        private SandboxedProcessInfo CreateProcessInfoWithKextConnection(Operation op)
        {
            var processInfo = ToProcessInfo(ToProcess(op));
            processInfo.SandboxedKextConnection = s_connection;

            return processInfo;
        }

        private SandboxedProcessMacKext CreateAndStartSandboxedProcess(SandboxedProcessInfo info, bool? measureTime = null)
        {
            var process = new SandboxedProcessMacKext(info, overrideMeasureTime: measureTime);
            process.Start();
            return process;
        }

        private void ContinouslyPostAccessReports(SandboxedProcessMacKext process, CancellationToken token, List<ReportInstruction> instructions = null)
        {
            Analysis.IgnoreResult(
                Task.Run(async () =>
                {
                    int count = 0;

                    while (true)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (instructions != null && count < instructions.Count)
                        {
                            PostAccessReport(instructions[count].Process, instructions[count].Operation,
                                            instructions[count].Stats, instructions[count].Pid,
                                            instructions[count].Path, instructions[count].Allowed);

                            // Advance the minimum enqueue time
                            s_connection.MinReportQueueEnqueueTime = instructions[count].Stats.EnqueueTime;

                            count++;
                        }

                        await Task.Delay(100);
                    }
                }, token)
                , justification: "Fire and forget"
            );
        }

        private static Sandbox.AccessReport PostAccessReport(SandboxedProcessMacKext proc, FileOperation operation, Sandbox.AccessReportStatistics stats,
                                                             int pid = 1234, string path = "/dummy/path", bool allowed = true)
        {
            var report = new Sandbox.AccessReport
            {
                Operation = operation,
                Statistics = stats,
                Pid        = pid,
                Path       = path,
                Status     = allowed ? (uint)FileAccessStatus.Allowed : (uint)FileAccessStatus.Denied
            };

            proc.PostAccessReport(report);
            return report;
        }
    }
}
