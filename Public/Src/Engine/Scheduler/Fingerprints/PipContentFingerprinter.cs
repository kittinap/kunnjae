// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.ContractsLight;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;

namespace BuildXL.Scheduler.Fingerprints
{
    /// <summary>
    /// Computes a <see cref="BuildXL.Engine.Cache.Fingerprints.ContentFingerprint" /> for given pips. 
    /// This fingerprint accounts for the <see cref="BuildXL.Cache.ContentStore.Hashing.ContentHash" /> of declared pip dependencies.
    /// </summary>
    /// <remarks>
    /// This class is thread safe.
    /// </remarks>
    public sealed class PipContentFingerprinter : PipFingerprinter
    {
        /// <summary>
        /// Creates an instance of <see cref="PipContentFingerprinter"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="PipContentFingerprinter"/> will look up the content of pip dependencies
        /// (when fingerprinting that pip) via the given callback. The new instance is thread-safe
        /// and so note that the callback may be called concurrently.
        /// </remarks>
        public PipContentFingerprinter(
            PathTable pathTable,
            PipFragmentRenderer.ContentHashLookup contentHashLookup,
            ExtraFingerprintSalts? extraFingerprintSalts = null,
            PathExpander pathExpander = null,
            PipDataLookup pipDataLookup = null)
            : base(pathTable, contentHashLookup, extraFingerprintSalts, pathExpander, pipDataLookup)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(contentHashLookup != null);
        }
    }
}
