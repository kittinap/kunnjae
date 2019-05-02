﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Threading.Tasks;
using BuildXL.Interop;
using BuildXL.Utilities;
using BuildXL.Utilities.Tasks;

namespace BuildXL.Processes
{
    /// <summary>
    /// Executes process asynchronously.
    /// </summary>
    public class AsyncProcessExecutor : IDisposable
    {
        private readonly TaskSourceSlim<Unit> m_processExitedTcs = TaskSourceSlim.Create<Unit>();
        private readonly TaskSourceSlim<Unit> m_stdoutFlushedTcs = TaskSourceSlim.Create<Unit>();
        private readonly TaskSourceSlim<Unit> m_stderrFlushedTcs = TaskSourceSlim.Create<Unit>();

        /// <summary>
        /// Underlying process
        /// </summary>
        public Process Process { get; private set; }

        /// <summary>
        /// Start time.
        /// </summary>
        public DateTime StartTime { get; private set; }

        /// <summary>
        /// Exit time.
        /// </summary>
        public DateTime ExitTime { get; private set; }

        /// <summary>
        /// Flag indicating if the process was killed.
        /// </summary>
        public bool Killed { get; private set; }

        /// <summary>
        /// Flag indicating if the process timed out.
        /// </summary>
        public bool TimedOut { get; private set; }

        /// <summary>
        /// Task that completes once this process dies.
        /// </summary>
        private Task WhenExited => m_processExitedTcs.Task;

        /// <summary>
        /// Checks if process exit is completed.
        /// </summary>
        public bool ExitCompleted => WhenExited.IsCompleted;

        /// <summary>
        /// Checks if standard out flush is completed.
        /// </summary>
        public bool StdOutCompleted => m_stdoutFlushedTcs.Task.IsCompleted;

        /// <summary>
        /// Checks if standard error flush is completed.
        /// </summary>
        public bool StdErrCompleted => m_stderrFlushedTcs.Task.IsCompleted;

        /// <summary>
        /// Timeout.
        /// </summary>
        private readonly TimeSpan m_timeout;

        /// <summary>
        /// Sandboxed process info for provenence purpose.
        /// </summary>
        private readonly SandboxedProcessInfo m_sandboxedProcessInfo;

        private int m_processId = -1;

        /// <summary>
        /// Process id.
        /// </summary>
        public int ProcessId => m_processId != -1 ? m_processId : (m_processId = Process.Id);

        /// <summary>
        /// Gets active peak memory usage.
        /// </summary>
        public ulong? GetActivePeakMemoryUsage()
        {
            try
            {
                if (Process == null || Process.HasExited)
                {
                    return null;
                }

                return Dispatch.GetActivePeakMemoryUsage(Process.Handle, ProcessId);
            }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                return null;
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        /// <summary>
        /// Creates an instance of <see cref="AsyncProcessExecutor"/>.
        /// </summary>
        public AsyncProcessExecutor(
            Process process, 
            TimeSpan timeout,
            Action<string> outputBuilder = null,
            Action<string> errorBuilder = null,
            SandboxedProcessInfo sandboxedProcessInfo = null)
        {
            Contract.Requires(process != null);

            Process = process;
            Process.Exited += (sender, e) => m_processExitedTcs.TrySetResult(Unit.Void);

            if (outputBuilder != null)
            {
                process.OutputDataReceived += (sender, e) => FeedOutputBuilder(m_stdoutFlushedTcs, e.Data, outputBuilder);
            }

            if (errorBuilder != null)
            {
                process.ErrorDataReceived += (sender, e) => FeedOutputBuilder(m_stderrFlushedTcs, e.Data, errorBuilder);
            }

            m_timeout = timeout;
            m_sandboxedProcessInfo = sandboxedProcessInfo;
        }

        /// <summary>
        /// Starts process.
        /// </summary>
        public void Start()
        {
            if (!System.IO.File.Exists(Process.StartInfo.FileName))
            {
                ThrowBuildXLException($"Process creation failed: File '{Process.StartInfo.FileName}' not found", new Win32Exception(0x2));
            }

            try
            {
                Process.Start();
            }
            catch (Win32Exception e)
            {
                ThrowBuildXLException($"Failed to start process '{Process.StartInfo.FileName}'", e);
            }

            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
            StartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Waits for process to exit or to get killed due to timed out.
        /// </summary>
        public async Task WaitForExitAsync(Func<Task> getProcessReport = null)
        {
            var finishedTask = await Task.WhenAny(Task.Delay(m_timeout), WhenExited);
            ExitTime = DateTime.UtcNow;

            var timedOut = finishedTask != WhenExited;
            if (timedOut)
            {
                TimedOut = true;
                await KillAsync();
            }

            if (getProcessReport != null)
            {
                await getProcessReport();
            }

            await Task.WhenAll(m_stdoutFlushedTcs.Task, m_stderrFlushedTcs.Task);
        }

        /// <summary>
        /// Kills process.
        /// </summary>
        public Task KillAsync()
        {
            Contract.Requires(Process != null);

            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                }
            }
            catch (Exception e) when (e is Win32Exception || e is InvalidOperationException)
            {
                // thrown if the process doesn't exist (e.g., because it has already completed on its own)
            }

            m_stdoutFlushedTcs.TrySetResult(Unit.Void);
            m_stderrFlushedTcs.TrySetResult(Unit.Void);
            Killed = true;

            return WhenExited;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Process?.Dispose();
        }

        private void ThrowBuildXLException(string message, Exception inner = null)
        {
            string description = m_sandboxedProcessInfo == null ? string.Empty : $"[Pip{m_sandboxedProcessInfo.PipSemiStableHash:X16} -- {m_sandboxedProcessInfo.PipDescription}] ";
            throw new BuildXLException($"{description}{message}", inner);
        }

        private static void FeedOutputBuilder(TaskSourceSlim<Unit> signalCompletion, string line, Action<string> eat)
        {
            if (signalCompletion.Task.IsCompleted)
            {
                return;
            }

            eat(line);

            if (line == null)
            {
                signalCompletion.TrySetResult(Unit.Void);
            }
        }
    }
}
