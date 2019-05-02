// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.WorkDispatcher;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Configuration;

namespace BuildXL.Scheduler.Distribution
{
    /// <summary>
    /// Handles choose worker computation for <see cref="Scheduler"/>
    /// </summary>
    internal class ChooseWorkerCacheLookup : ChooseWorkerContext
    {
        private long m_cacheLookupWorkerRoundRobinCounter;

        public ChooseWorkerCacheLookup(
            LoggingContext loggingContext,
            IConfiguration config,
            IReadOnlyList<Worker> workers,
            IPipQueue pipQueue) : base(loggingContext, config, workers, pipQueue, DispatcherKind.ChooseWorkerCacheLookup, config.Schedule.MaxChooseWorkerCacheLookup)
        {
        }

        /// <summary>
        /// Choose a worker based on setup cost
        /// </summary>
        protected override Task<Worker> ChooseWorkerCore(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.PipType == PipType.Process);

            var processRunnable = (ProcessRunnablePip)runnablePip;
            if (Config.Distribution.DistributeCacheLookups && AnyRemoteWorkers)
            {
                var startWorkerOffset = Interlocked.Increment(ref m_cacheLookupWorkerRoundRobinCounter);

                for (int i = 0; i < Workers.Count; i++)
                {
                    var workerId = (i + startWorkerOffset) % Workers.Count;
                    var worker = Workers[(int)workerId];
                    if (worker.TryAcquireCacheLookup(processRunnable, force: false))
                    {
                        return Task.FromResult(worker);
                    }
                }

                return Task.FromResult((Worker)null);
            }

            var acquired = LocalWorker.TryAcquireCacheLookup(processRunnable, force: true);
            Contract.Assert(acquired, "The local worker must be acquired for cache lookup when force=true");

            return Task.FromResult((Worker)LocalWorker);
        }
    }
}
