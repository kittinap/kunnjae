// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Reflection;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Logging;

namespace BuildXL.Cache.ContentStore.Logging
{
    /// <summary>
    ///     An ILog that appends messages to a log file in the TEMP\ContentStore folder.
    /// </summary>
    public class FileLog : ILog
    {
        private static readonly string[] SeverityNames =
        {
            "UNKWN",
            "DIAG ",
            "DEBUG",
            "INFO ",
            "WARN ",
            "ERROR",
            "FATAL",
            "ALWAY"
        };

        private readonly object _syncObject = new object();
        private readonly int _maxFileCount;
        private readonly long _maxFileSize;
        private readonly bool _autoFlush;
        private readonly int _fileNumberWidth;
        private readonly List<string> _paths;
        private StreamWriter _textWriter;
        private int _fileNumber;
        private int _fileCount;

        /// <summary>
        ///     Initializes a new instance of the <see cref="FileLog" /> class using the given directory path.
        /// </summary>
        /// <param name="logDirectoryPath">Existing directory where log file is created.</param>
        /// <param name="logFileBaseName">Base name of log file to use or null to have default used.</param>
        /// <param name="severity">Entries below this severity are ignored.</param>
        /// <param name="autoFlush">If true, flush after every write; otherwise do not with better performance.</param>
        /// <param name="maxFileSize">Maximum size per file. If positive, log rolling is enabled.</param>
        /// <param name="maxFileCount">Maximum number of files to allow. If zero, allow any number of files.</param>
        /// <param name="dateInFileName">If true, append the current date/time in the filename.</param>
        /// <param name="processIdInFileName">If true, append the current process id in the filename.</param>
        public FileLog
            (
            string logDirectoryPath,
            string logFileBaseName,
            Severity severity = Severity.Diagnostic,
            bool autoFlush = false,
            long maxFileSize = 0,
            int maxFileCount = 0,
            bool dateInFileName = true,
            bool processIdInFileName = true
            )
            : this
                (
                GetLogFilePath(logDirectoryPath, logFileBaseName, dateInFileName, processIdInFileName),
                severity,
                autoFlush,
                maxFileSize,
                maxFileCount
                )
        {
            Contract.Requires(maxFileSize >= 0);
            Contract.Requires(maxFileCount >= 0);
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="FileLog" /> class using the given file path.
        /// </summary>
        /// <param name="logFilePath">Full path to log file.</param>
        /// <param name="severity">Entries below this severity are ignored.</param>
        /// <param name="autoFlush">If true, flush after every write; otherwise do not with better performance.</param>
        /// <param name="maxFileSize">Maximum size per file. If positive, log rolling is enabled.</param>
        /// <param name="maxFileCount">Maximum number of files to allow. If zero, allow any number of files.</param>
        public FileLog
            (
            string logFilePath,
            Severity severity = Severity.Diagnostic,
            bool autoFlush = false,
            long maxFileSize = 0,
            int maxFileCount = 0
            )
        {
            Contract.Requires(logFilePath != null);
            Contract.Requires(!string.IsNullOrEmpty(Path.GetDirectoryName(logFilePath)));
            Contract.Requires(maxFileSize >= 0);
            Contract.Requires(maxFileCount >= 0);
            Contract.Requires((maxFileCount > 0).Implies(maxFileSize > 0));

            FilePath = logFilePath;
            CurrentSeverity = severity;
            _maxFileSize = maxFileSize;
            _maxFileCount = maxFileCount;
            _autoFlush = autoFlush;

            var directoryPath = Path.GetDirectoryName(FilePath);
            if (directoryPath == null)
            {
                throw new DirectoryNotFoundException(FilePath);
            }

            Directory.CreateDirectory(directoryPath);

            _fileCount = 1;
            _fileNumber = maxFileSize == 0 ? -1 : 0;
            _fileNumberWidth = maxFileCount == 0 ? 0 : (int)Math.Log10(maxFileCount) + 1;

            string filePath;
            _textWriter = CreateStreamWriter(FilePath, _fileNumber, _fileNumberWidth, out filePath);
            _paths = new List<string> {filePath};
        }

        /// <summary>
        ///     Gets full path to log file.
        /// </summary>
        public string FilePath { get; }

        /// <inheritdoc />
        public Severity CurrentSeverity { get; }

        /// <inheritdoc />
        public void Flush()
        {
            lock (_syncObject)
            {
                _textWriter.Flush();
            }
        }

        /// <inheritdoc />
        public void Write(DateTime dateTime, int threadId, Severity severity, string message)
        {
            if (severity < CurrentSeverity)
            {
                return;
            }

            var severityName = SeverityNames[(int)severity];
            var line = string.Format
                (
                    CultureInfo.CurrentCulture,
                    "{0:yyyy-MM-dd HH:mm:ss,fff} {1,3} {2} {3}",
                    dateTime,
                    threadId,
                    severityName,
                    message
                );

            lock (_syncObject)
            {
                if (_maxFileSize > 0 && _textWriter.BaseStream.Position > _maxFileSize)
                {
                    _textWriter.Dispose();
                    _fileCount++;

                    string filePath;
                    _textWriter = CreateStreamWriter(FilePath, ++_fileNumber, _fileNumberWidth, out filePath);
                    _paths.Add(filePath);

                    if (_maxFileCount > 0 && _fileCount > _maxFileCount)
                    {
                        var oldestPath = _paths[0];
                        _paths.RemoveAt(0);
                        _fileCount--;

                        try
                        {
                            File.Delete(oldestPath);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }

                WriteLine(severity, severityName, line);

                if (_autoFlush)
                {
                    _textWriter.Flush();
                }
            }
        }

        /// <nodoc />
        public virtual void WriteLine(Severity severity, string severityName, string message)
        {
            _textWriter.WriteLine(message);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_syncObject)
            {
                _textWriter.Dispose();
            }
        }

        private static StreamWriter CreateStreamWriter(string logFilePath, int fileNumber, int fileNumberWidth, out string filePath)
        {
            filePath = fileNumber >= 0
                ? BuildRolledLogFilePath(logFilePath, fileNumber, fileNumberWidth)
                : logFilePath;

            // StreamWriter.Dispose will dispose its stream.
            var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);

            // Create a StreamWriter with UTF-8 encoding without a Byte-Order Mark (BOM).
            return new StreamWriter(fileStream);
        }

        private static string BuildRolledLogFilePath(string logFilePath, int fileNumber, int fileNumberWidth)
        {
            // ReSharper disable AssignNullToNotNullAttribute
            var directoryPath = Path.GetDirectoryName(logFilePath);
            var currentBaseName = Path.GetFileNameWithoutExtension(logFilePath);
            var currentExtension = Path.GetExtension(logFilePath);
            var file = fileNumberWidth == 0
                ? fileNumber.ToString(CultureInfo.InvariantCulture)
                : fileNumber.ToString(CultureInfo.InvariantCulture).PadLeft(fileNumberWidth, '0');
            return Path.Combine(
                directoryPath, string.Format(CultureInfo.InvariantCulture, "{0}-{1}{2}", currentBaseName, file, currentExtension));

            // ReSharper restore AssignNullToNotNullAttribute
        }

        private static string GetDefaultLogDirPath()
        {
            return Path.Combine(Path.GetTempPath(), "CloudStore");
        }

        private static string GetLogFilePath(string logDirectoryPath, string logFileBaseName, bool dateInFileName, bool processIdInFileName)
        {
            // Build log filename and path to log directory, making sure it exists.
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var fileBaseName = logFileBaseName ?? Path.GetFileNameWithoutExtension(assembly.Location);

            if (dateInFileName)
            {
                fileBaseName += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss.fff", CultureInfo.CurrentCulture);
            }

            if (processIdInFileName)
            {
                fileBaseName += "-" + Process.GetCurrentProcess().Id;
            }

            return Path.Combine(logDirectoryPath ?? GetDefaultLogDirPath(), fileBaseName + ".log");
        }
    }
}
