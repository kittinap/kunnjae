// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Utilities;
using BuildXL.Utilities.Tracing;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTest.BuildXL.Scheduler
{
    [Trait("Category", "SharedOpaqueDirectoryTests")]
    [Feature(Features.SharedOpaqueDirectory)]
    public class CompositeSharedOpaqueDirectoryTests : SchedulerIntegrationTestBase
    {
        public CompositeSharedOpaqueDirectoryTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void CompositeSharedOpaqueDirectoryConsumptionCachingBehavior()
        {
            CreatePip(
                @"root\pipA",
                out FileArtifact inputA, 
                out FileArtifact outputA,
                out ProcessWithOutputs pipA, 
                out DirectoryArtifact soA);

            CreatePip(
                @"root\pipB",
                out FileArtifact inputB,
                out FileArtifact outputB,
                out ProcessWithOutputs pipB,
                out DirectoryArtifact soB);

            var root = AbsolutePath.Create(Context.PathTable, Path.Combine(ObjectRoot, "root"));

            // We create a composite shared opaque containing both shared opaques
            var result = PipConstructionHelper.TryComposeSharedOpaqueDirectory(root, new[] { soA, soB }, description: null, tags: new string[] { }, out var composedOpaque);
            XAssert.IsTrue(result);

            // PipC consumes the composed shared opaque and reads from both outputs
            var builder = CreatePipBuilder(new List<Operation>
                             {
                                 Operation.ReadFile(outputA, doNotInfer: true),
                                 Operation.ReadFile(outputB, doNotInfer: true),
                                 Operation.WriteFile(CreateOutputFileArtifact())
                             });
            builder.AddInputDirectory(composedOpaque);
            var pipC = SchedulePipBuilder(builder);

            // First time all should miss, second time all should hit
            RunScheduler().AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId, pipC.Process.PipId).AssertSuccess();
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId, pipC.Process.PipId);

            // Modify inputA and make sure that only pipA and PipC re-run
            File.WriteAllText(ArtifactToString(inputA), "New content");
            RunScheduler().AssertCacheMiss(pipA.Process.PipId, pipC.Process.PipId).AssertCacheHit(pipB.Process.PipId);
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId, pipC.Process.PipId);
        }

        [Fact]
        public void CompositeSharedOpaqueDirectoryReadBehavior()
        {
            CreatePip(
                @"root\pipA",
                out FileArtifact _,
                out FileArtifact outputA,
                out ProcessWithOutputs _,
                out DirectoryArtifact soA);

            CreatePip(
                @"root\pipB",
                out FileArtifact _,
                out FileArtifact outputB,
                out ProcessWithOutputs _,
                out DirectoryArtifact soB);

            var root = AbsolutePath.Create(Context.PathTable, Path.Combine(ObjectRoot, "root"));

            // We construct a composite shared opaque that only constains soa (and *not* sob)
            var result = PipConstructionHelper.TryComposeSharedOpaqueDirectory(root, new[] { soA }, description: null, tags: new string[] { }, out var composedOpaque);
            XAssert.IsTrue(result);

            // PipC consumes the composed shared opaque and reads from both outputs
            var builder = CreatePipBuilder(new List<Operation>
                             {
                                 Operation.ReadFile(outputA, doNotInfer: true),
                                 Operation.ReadFile(outputB, doNotInfer: true), // this one should be disallowed
                                 Operation.WriteFile(CreateOutputFileArtifact())
                             });
            builder.AddInputDirectory(composedOpaque);
            var pipC = SchedulePipBuilder(builder);

            IgnoreWarnings();
            // pipC should not be allowed to read from outputB, since the composed shared opaque does not contain it
            RunScheduler().AssertFailure();
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void DuplicateContentIsAllowedInCompositeSharedOpaqueDirectory()
        {
            CreatePip(
                @"root\pipA",
                out FileArtifact _,
                out FileArtifact outputA,
                out ProcessWithOutputs _,
                out DirectoryArtifact soA);

            var root = AbsolutePath.Create(Context.PathTable, Path.Combine(ObjectRoot, "root"));

            // We construct a composite shared opaque that constains soA twice
            var result = PipConstructionHelper.TryComposeSharedOpaqueDirectory(root, new[] { soA, soA }, description: null, tags: new string[] { }, out var composedOpaque);
            XAssert.IsTrue(result);

            // PipB consumes the composed shared opaque and reads from outputA
            var builder = CreatePipBuilder(new List<Operation>
                             {
                                 Operation.ReadFile(outputA, doNotInfer: true),
                                 Operation.WriteFile(CreateOutputFileArtifact())
                             });
            builder.AddInputDirectory(composedOpaque);
            var pipB = SchedulePipBuilder(builder);

            IgnoreWarnings();
            
            RunScheduler().AssertSuccess();
        }

        /// <summary>
        /// Creates a <paramref name="pip"/> that reads from <paramref name="input"/> and writes to <paramref name="output"/>.
        /// The output id written in a <paramref name="sharedOpaque"/> directory with root <paramref name="root"/>.
        /// </summary>
        private void CreatePip(string root, out FileArtifact input, out FileArtifact output, out ProcessWithOutputs pip, out DirectoryArtifact sharedOpaque)
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, root);
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            output = CreateOutputFileArtifact(sharedOpaqueDir);
            input = CreateSourceFile();
            pip = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, input, new KeyValuePair<FileArtifact, string>(output, null));
            sharedOpaque = pip.ProcessOutputs.GetOutputDirectories().Single().Root;
        }
    }
}
