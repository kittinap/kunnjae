// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Utilities.Tracing;

#if !FEATURE_MICROSOFT_DIAGNOSTICS_TRACING
using System.Diagnostics.Tracing;
#endif
#if FEATURE_MICROSOFT_DIAGNOSTICS_TRACING
#endif

namespace Test.BuildXL.TestUtilities.Xunit
{
    /// <inheritdoc/>
    public sealed class TestEventListener : TestEventListenerBase
    {
        /// <nodoc/>
        public TestEventListener(Events eventSource, string fullyQualifiedTestName, bool captureAllDiagnosticMessages = true, Action<string> logAction = null)
            : base(eventSource, fullyQualifiedTestName, captureAllDiagnosticMessages, logAction) { }

        /// <inheritdoc/>
        protected override void AssertTrue(bool condition, string format, params string[] args)
        {
            XAssert.IsTrue(condition, format, args);
        }
    }
}
