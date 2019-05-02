// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Execution.Analyzer.Analyzers.Simulator;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler;
using BuildXL.Scheduler.Graph;
using BuildXL.Scheduler.Tracing;
using BuildXL.ToolSupport;
using BuildXL.Utilities;

namespace BuildXL.Execution.Analyzer
{
    internal partial class Args
    {
        public Analyzer InitializeBuildSimulatorAnalyzer(AnalysisInput analysisInput)
        {
            string outputDirectory = null;
            var simulator = new BuildSimulatorAnalyzer(analysisInput);

            foreach (var opt in AnalyzerOptions)
            {
                if (opt.Name.Equals("outputDirectory", StringComparison.OrdinalIgnoreCase) ||
                   opt.Name.Equals("o", StringComparison.OrdinalIgnoreCase))
                {
                    simulator.OutputDirectory = ParseSingletonPathOption(opt, outputDirectory);
                }
                else if (opt.Name.Equals("step", StringComparison.OrdinalIgnoreCase) ||
                   opt.Name.Equals("s", StringComparison.OrdinalIgnoreCase))
                {
                    simulator.IncludedExecutionSteps[(int)ParseEnumOption<PipExecutionStep>(opt)] = true;
                }
                else
                {
                    throw Error("Unknown option for build simulation analysis: {0}", opt.Name);
                }
            }

            return simulator;
        }

        private static void WriteBuildSimulatorHelp(HelpWriter writer)
        {
            writer.WriteBanner("Build Simulation Analysis");
            writer.WriteModeOption(nameof(AnalysisMode.Simulate), "EXPERIMENTAL. Build Simulator");
            writer.WriteOption("outputFile", "Required. The file where to write the results", shortName: "o");
        }
    }

    /// <summary>
    /// Analyzer used to compute the reason for cache misses
    /// </summary>
    internal sealed class BuildSimulatorAnalyzer : Analyzer
    {
        /// <summary>
        /// The path to the output file
        /// </summary>
        public string OutputDirectory;

        public bool[] IncludedExecutionSteps = new bool[EnumTraits<PipExecutionStep>.MaxValue + 1];
        private static string resultsFormat;

        private readonly PipExecutionData m_executionData = new PipExecutionData();

        public BuildSimulatorAnalyzer(AnalysisInput input)
            : base(input)
        {
            m_executionData.CachedGraph = input.CachedGraph;
        }

        public override void Prepare()
        {
            // Always include execution
            IncludedExecutionSteps[(int)PipExecutionStep.ExecuteProcess] = true;
            IncludedExecutionSteps[(int)PipExecutionStep.ExecuteNonProcessPip] = true;
            resultsFormat = @"{0}\sim\{{0}}".FormatWith(OutputDirectory);

            base.Prepare();
        }

        public override void PipExecutionStepPerformanceReported(PipExecutionStepPerformanceEventData data)
        {
            var startTime = (ulong)data.StartTime.Ticks;
            var endTime = (ulong)(data.StartTime + data.Duration).Ticks;
            startTime.Min(ref m_executionData.MinStartTime);
            endTime.Max(ref m_executionData.MaxEndTime);

            if (!IncludedExecutionSteps[(int)data.Step])
            {
                return;
            }

            var currentStartTime = m_executionData.StartTimes[data.PipId.ToNodeId()];
            if (currentStartTime == 0 || currentStartTime > startTime)
            {
                m_executionData.StartTimes[data.PipId.ToNodeId()] = startTime;
            }

            m_executionData.Durations[data.PipId.ToNodeId()] += endTime - startTime;
        }

        public override int Analyze()
        {
            Console.WriteLine("Analyzing");

            var data = m_executionData;
            data.Compute();
            Directory.CreateDirectory(resultsFormat.FormatWith(string.Empty));

            int simulationCount = 24;
            int increment = 100;

            using (StreamWriter sw = new StreamWriter(resultsFormat.FormatWith("result.txt")))
            {
                int actualConcurrency = data.ActualConcurrency <= 0 ? increment : data.ActualConcurrency;

                // write result.txt
                var writers = new MultiWriter(sw, Console.Out);

                //SimulateActual(data, actualConcurrency, writers);

                // a Pip is a process (entity of invocation) or a target for CB
                int nameWidth = data.LongestRunningPips.Select(n => data.GetName(n.Node).Length).Max();
                writers.WriteLine();
                writers.WriteLine("Top 20 Long Running Pips:");
                foreach (var p in data.LongestRunningPips)
                {
                    writers.WriteLine("{0} [{2}]: {1} min", data.GetName(p.Node).PadLeft(nameWidth), p.Priority.ToMinutes(), data.PipIds[p.Node]);
                }

                writers.WriteLine();
                writers.WriteLine("Bottom 20 Shortest Running Processes:");
                foreach (var p in data.ShortestRunningProcesses)
                {
                    writers.WriteLine("{0} [{2}]: {1} min", data.GetName(p.Node).PadLeft(nameWidth), p.Priority.ToMinutes(), data.PipIds[p.Node]);
                }

                SimulationResult[] results = SimulateBuildWithVaryingThreatCount(data, simulationCount, increment);

                results = results.OrderBy(s => s.Threads.Count).ToArray();

                string format = "{0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}";
                writers.WriteLine();
                writers.WriteLine("Results");
                writers.WriteLine(
                    format,
                    "Thread Count", // 0
                    "Total Execution Time (min)", // 1
                    "Critical Path Length", // 2
                    "Critical Path Tail Pip", // 3
                    "Critical Path Idle Time (min)", // 4
                    "Total Active time (min)", // in minutes //5
                    "Total time (min)", // TotalTime 6
                    "Average Utilization", // 7
                    "Max Path Length", // 8
                    "Executed Pips") // 9
                    ;

                // the thread that took the most time excuting
                // cp in this case is not the actual critical path, but the thread that took the longest or the last to complete
                // even if the cp is 20 minutes, the cp as a thread in this context could be 30 due to limited ressources (machines/cores)
                // in terms of time the thread cp will be the same time as the actual cp
                // this information is per build
                for (int i = 0; i < results.Length; i++)
                {
                    var result = results[i];
                    writers.WriteLine(
                        format,
                        result.Threads.Count, // Thread Count: number of cores that have been used
                        result.TotalTime.ToMinutes(), // Total Execution Time (min): minutes that it took for the build
                        result.ThreadWithLatestEndTime.Executions.Count, // Critical Path Length:
                        result.ThreadWithLatestEndTime.Executions.Last().Id.Value, // Critical Path Tail Pip:
                        result.ThreadWithLatestEndTime.IdleTime.ToMinutes(), // Critical Path Idle Time (min)
                        result.TotalActiveTime.ToMinutes(), // totalactivetime in minutes
                        result.TotalTime.ToMinutes(),                     // result.TotalTime
                        Math.Round((double)result.TotalActiveTime / (double)((ulong)result.Threads.Count * result.TotalTime), 3), // Average Utilization
                        result.Threads.Select(t => t.Executions.Count).Max(),
                        result.Threads.Select(t => t.Executions.Count).Sum());
                }

                // Console.ReadKey();
            }

            return 0;
        }

        private static void SimulateActual(PipExecutionData data, int actualConcurrency, MultiWriter writers)
        {
            // simulate with actual concurrency
            Console.WriteLine("Simulating actual build");
            SimulationResult actualSimulation = new SimulationResult(data, data.AggregateCosts);
            actualSimulation.Simulate(actualConcurrency);
            Console.WriteLine("Done");
            Console.WriteLine();

            WriteActualAndSimulationResults(data, actualSimulation);


            writers.WriteLine("Edge Count: {0}", data.DataflowGraph.EdgeCount);
            writers.WriteLine("Pip Type Counts:");
            foreach (var pipType in EnumTraits<PipType>.EnumerateValues())
            {
                writers.WriteLine("{0}: {1}", data.PipTypeCounts[(int)pipType].ToString().PadLeft(10), pipType);
            }

            writers.WriteLine("Processes with timing information:{0} ", data.DataflowGraph.Nodes.Where(node => data.GetPipType(node) == PipType.Process && data.StartTimes[node] != 0).Count());

            writers.WriteLine("Actual Total Build Time: {0} min", data.TotalDuration.ToMinutes());
            writers.WriteLine("Actual Concurrency: {0}", data.ActualConcurrency);
            writers.WriteLine("Simulated total build time (using actual concurrency): {0} min", actualSimulation.TotalTime.ToMinutes());

            // write down info for each critical path
            ulong criticalPathCost = WriteCriticalPathToResult(writers, data, actualSimulation);
        }

        private static SimulationResult[] SimulateBuildWithVaryingThreatCount(PipExecutionData data, int simulationCount, int increment)
        {
            SimulationResult[] results = new SimulationResult[simulationCount];
            int?[] threadCounts = new int?[simulationCount];
            Parallel.For(0, simulationCount, i =>
            {
                var threadCount = threadCounts[i] ?? ((i + 1) * increment);
                Console.WriteLine("Simulating {0}...", i);
                SimulationResult result = new SimulationResult(data, data.AggregateCosts);
                result.Simulate(threadCount);
                results[i] = result;
                Console.WriteLine("Done {0}", i);
            });

            return results;
        }

        private static void WriteActualAndSimulationResults(PipExecutionData data, SimulationResult actualSimulation)
        {
            // write results actual
            File.WriteAllLines(resultsFormat.FormatWith("actual.durations.csv"), data.Spans.Where(ps => data.GetPipType(ps.Id) == PipType.Process).Select(ps => string.Join(",", data.GetName(ps.Id), ps.Duration.ToMinutes().ToString())));
            File.WriteAllLines(resultsFormat.FormatWith("actual.durations.txt"), data.Spans.Select(ps => ps.Duration.ToMinutes().ToString()));
            File.WriteAllLines(resultsFormat.FormatWith("actual.starts.txt"), data.Spans.Select(ps => ps.StartTime.ToMinutes().ToString()));
            File.WriteAllLines(resultsFormat.FormatWith("actual.ends.txt"), data.Spans.Select(ps => ps.EndTime.ToMinutes().ToString()));

            // write simulation actual
            File.WriteAllLines(resultsFormat.FormatWith("actualSimulation.durations.txt"), actualSimulation.GetSpans().Select(ps => ps.Duration.ToMinutes().ToString()));
            File.WriteAllLines(resultsFormat.FormatWith("actualSimulation.starts.txt"), actualSimulation.GetSpans().Select(ps => ps.StartTime.ToMinutes().ToString()));
            File.WriteAllLines(resultsFormat.FormatWith("actualSimulation.ends.txt"), actualSimulation.GetSpans().Select(ps => ps.EndTime.ToMinutes().ToString()));
            File.WriteAllLines(resultsFormat.FormatWith("heights.txt"), data.Spans.Where(ps => data.GetPipType(ps.Id) == PipType.Process)
                .Select(ps => data.DataflowGraph.GetNodeHeight(ps.Id))
                .GroupBy(i => i)
                .OrderBy(g => g.Key)
                .Select(g => $"Height: {g.Key}, Count: {g.Count()}"));

            // information on each process during simulation
            string csvFormat = "{0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}";
            File.WriteAllLines(resultsFormat.FormatWith("actualSimulation.txt"), new string[] { csvFormat.FormatWith("Id", "Thread", "Minimum Start Time", "Start Time", "End Time", "Duration", "Incoming", "Outgoing") }.Concat(actualSimulation.GetSpans().Select(ps =>
                csvFormat.FormatWith(
                    ps.Id.Value,
                    ps.Thread,
                    actualSimulation.MinimumStartTimes[ps.Id].ToMinutes(),
                    ps.StartTime.ToMinutes(),
                    ps.EndTime.ToMinutes(),
                    ps.Duration.ToMinutes(),
                    data.DataflowGraph.GetIncomingEdgesCount(ps.Id),
                    data.DataflowGraph.GetIncomingEdgesCount(ps.Id)))));
        }

        private static ulong WriteCriticalPathToResult(MultiWriter writers, PipExecutionData data, SimulationResult testSimulation)
        {
            int nameWidth;
            int count = 0;
            ulong criticalPathCost = 0;
            foreach (var p in data.GetSortedPips(2, true, n => data.GetPipType(n) == PipType.Process, n => data.AggregateCosts[n]))
            {
                // this is the actual critical path. This is not associated with the simulation as it only correlates to the dgg
                List<NodeId> criticalPath = new List<NodeId>();
                NodeId criticalChainNode = p.Node;
                while (criticalChainNode.IsValid)
                {
                    criticalPath.Add(criticalChainNode);
                    criticalChainNode = data.CriticalChain[criticalChainNode];
                }

                p.Priority.Max(ref criticalPathCost);

                writers.WriteLine("Critical Path {0}:", count++);
                writers.WriteLine("Critical Path Cost: {0} min", p.Priority.ToMinutes());
                writers.WriteLine("Critical Path Length: {0}", criticalPath.Count);
                nameWidth = criticalPath.Select(n => data.GetName(n).Length).Max();
                writers.WriteLine("Critical Path:\n    {0}", string.Join(Environment.NewLine + " -> ", criticalPath

                    // .Where(n => data.Durations[n] > 0)
                    .Select(n => string.Format(
                        "{0} ({1} min) [{2}] <{3}> [{4},{5}]",

                    // .Select(n => string.Format("{0} ({1} min) [{2}] <{3}>",
                        data.GetName(n).PadLeft(nameWidth),
                        data.Durations[n].ToMinutes(),
                        data.PipIds[n],
                        data.GetPipType(n),
                        testSimulation.StartTimes[n].ToMinutes(),
                        testSimulation.EndTimes[n].ToMinutes()))));

                // data.PipTypes[n]))));
            }

            return criticalPathCost;
        }
    }
}
