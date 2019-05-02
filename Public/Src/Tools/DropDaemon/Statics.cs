// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using BuildXL.Native.IO;

namespace Tool.DropDaemon
{
    /// <summary>
    ///     Various helper method, typically to be imported with "using static".
    /// </summary>
    public static class Statics
    {
        /// <summary>
        ///     String format with <see cref="CultureInfo.InvariantCulture"/>.
        /// </summary>
        public static string Inv(string format, params object[] args)
        {
            Contract.Requires(format != null);

            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        /// <summary>
        ///     Logs an error as a line of text.  Currently prints out to <code>Console.Error</code>.
        ///     to use whatever other
        /// </summary>
        public static void Error(string format, params object[] args)
        {
            if (format != null)
            {
                Console.Error.WriteLine(Inv(format, args));
            }
        }

        /// <summary>
        ///     Returns whether the file at a given location is a symlink.
        /// </summary>
        public static bool IsSymLinkOrMountPoint(string absoluteFilePath)
        {
            var reparsePointType = FileUtilities.TryGetReparsePointType(absoluteFilePath);
            if (!reparsePointType.Succeeded)
            {
                return false;
            }

            return FileUtilities.IsReparsePointActionable(reparsePointType.Result);
        }
    }
}
