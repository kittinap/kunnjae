// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Utilities;

namespace BuildXL.ToolSupport
{
    /// <summary>
    /// Interface that indicates a class can parse arguments
    /// </summary>
    /// <remarks>
    /// The implementations of this interface are generally generated by BuildXL.ToolSupport.Generator.exe
    /// </remarks>
    public interface IArgumentParser<TToolParameterValue>
    {
        /// <summary>
        /// Attempt to parse the command line
        /// </summary>
        /// <remarks>
        /// Implementor should return false when there was a failure.
        /// Implementor is responsible for reporting helpful messages about the arguments that violate the contract of the IToolParameterValue.
        /// </remarks>
        bool TryParse(string[] args, PathTable pathTable, out TToolParameterValue arguments);
    }
}
