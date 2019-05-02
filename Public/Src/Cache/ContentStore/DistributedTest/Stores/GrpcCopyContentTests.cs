﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using ContentStoreTest.Extensions;
using ContentStoreTest.Test;
using Xunit;

namespace ContentStoreTest.Distributed.Stores
{
    [Trait("Category", "WindowsOSOnly")]
    public class GrpcCopyContentTests : TestBase
    {
        private const int FileSize = 1000;
        private const HashType DefaultHashType = HashType.Vso0;
        private Context _context;

        public GrpcCopyContentTests()
            : base(() => new PassThroughFileSystem(TestGlobal.Logger), TestGlobal.Logger)
        {
            _context = new Context(Logger);
        }

        [Fact]
        public async Task CopyExistingFile()
        {
            await RunTestCase(nameof(CopyExistingFile), async (rootPath, session, client) =>
            {
                // Write a random file
                var sourcePath = rootPath / ThreadSafeRandom.Generator.Next().ToString();
                var content = ThreadSafeRandom.GetBytes(FileSize);
                FileSystem.WriteAllBytes(sourcePath, content);

                // Put the random file
                PutResult putResult = await session.PutFileAsync(_context, HashType.Vso0, sourcePath, FileRealizationMode.Any, CancellationToken.None);
                putResult.ShouldBeSuccess();

                // Copy the file out via GRPC
                var destinationPath = rootPath / ThreadSafeRandom.Generator.Next().ToString();
                (await client.CopyFileAsync(_context, putResult.ContentHash, destinationPath, CancellationToken.None)).ShouldBeSuccess();

                var copied = FileSystem.ReadAllBytes(destinationPath);

                // Compare original and copied files
                var originalHash = content.CalculateHash(DefaultHashType);
                var copiedHash = copied.CalculateHash(DefaultHashType);
                Assert.Equal(originalHash, copiedHash);
            });
        }

        [Fact]
        public async Task CheckExistingFile()
        {
            await RunTestCase(nameof(CheckExistingFile), async (rootPath, session, client) =>
            {
                // Write a random file
                var sourcePath = rootPath / ThreadSafeRandom.Generator.Next().ToString();
                var content = ThreadSafeRandom.GetBytes(FileSize);
                FileSystem.WriteAllBytes(sourcePath, content);

                // Put the random file
                PutResult putResult = await session.PutFileAsync(_context, HashType.Vso0, sourcePath, FileRealizationMode.Any, CancellationToken.None);
                putResult.ShouldBeSuccess();

                // Check if file exists
                (await client.CheckFileExistsAsync(_context, putResult.ContentHash)).ShouldBeSuccess();
            });
        }

        [Fact]
        public async Task CopyNonExistingFile()
        {
            await RunTestCase(nameof(CopyNonExistingFile), async (rootPath, session, client) =>
            {
                // Copy the file out via GRPC
                var copyFileResult = await client.CopyFileAsync(_context, ContentHash.Random(), rootPath / ThreadSafeRandom.Generator.Next().ToString(), CancellationToken.None);

                Assert.False(copyFileResult.Succeeded);
                Assert.Equal(CopyFileResult.ResultCode.FileNotFoundError, copyFileResult.Code);
            });
        }

        [Fact]
        public async Task CheckNonExistingFile()
        {
            await RunTestCase(nameof(CheckNonExistingFile), async (rootPath, session, client) =>
            {
                // Check if random non-existent file exists
                (await client.CheckFileExistsAsync(_context, ContentHash.Random())).ShouldBeError();
            });
        }

        [Fact]
        public async Task WrongPort()
        {
            await RunTestCase(nameof(WrongPort), async (rootPath, session, client) =>
            {
                // Copy fake file out via GRPC
                var host = "localhost";
                var bogusPort = PortExtensions.GetNextAvailablePort();
                using (client = GrpcCopyClient.Create(host, bogusPort))
                {
                    var copyFileResult = await client.CopyFileAsync(_context, ContentHash.Random(), rootPath / ThreadSafeRandom.Generator.Next().ToString(), CancellationToken.None);
                    Assert.Equal(CopyFileResult.ResultCode.SourcePathError, copyFileResult.Code);
                }
            });
        }

        private async Task RunTestCase(string testName, Func<AbsolutePath, IContentSession, GrpcCopyClient, Task> testAct)
        {
            var cacheName = testName + "_cache";

            using (var directory = new DisposableDirectory(FileSystem))
            {
                var rootPath = directory.Path;

                var namedCacheRoots = new Dictionary<string, AbsolutePath> { { cacheName, rootPath } };
                var grpcPort = PortExtensions.GetNextAvailablePort();
                var grpcPortFileName = Guid.NewGuid().ToString();
                var configuration = new ServiceConfiguration(
                    namedCacheRoots,
                    rootPath,
                    42,
                    ServiceConfiguration.DefaultGracefulShutdownSeconds,
                    grpcPort,
                    grpcPortFileName);

                var storeConfig = ContentStoreConfiguration.CreateWithMaxSizeQuotaMB(1);
                Func<AbsolutePath, IContentStore> contentStoreFactory = (path) =>
                    new FileSystemContentStore(
                        FileSystem,
                        SystemClock.Instance,
                        directory.Path,
                        new ConfigurationModel(storeConfig));

                var server = new LocalContentServer(FileSystem, Logger, testName, contentStoreFactory, new LocalServerConfiguration(configuration));

                await server.StartupAsync(_context).ShouldBeSuccess();
                var createSessionResult = await server.CreateSessionAsync(new OperationContext(_context), testName, cacheName, ImplicitPin.PutAndGet, Capabilities.ContentOnly);
                createSessionResult.ShouldBeSuccess();

                (int sessionId, AbsolutePath tempDir) = createSessionResult.Value;
                var session = server.GetSession(sessionId);

                // Create a GRPC client to connect to the server
                var port = new MemoryMappedFilePortReader(grpcPortFileName, Logger).ReadPort();
                using (var client = GrpcCopyClient.Create("localhost", port))
                {
                    // Run validation
                    await testAct(rootPath, session, client);
                }

                await server.ShutdownAsync(_context).ShouldBeSuccess();
            }
        }
    }
}
