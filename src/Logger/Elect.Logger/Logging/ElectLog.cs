﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Elect.Core.ConcurrentUtils;
using Elect.Core.ObjUtils;
using Elect.Logger.Logging.Models;
using Elect.Logger.Models.Logging;
using Humanizer;
using JsonFlatFileDataStore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Elect.Logger.Logging
{
    public class ElectLog : ElectMessageQueue<LogModel>, IElectLog
    {
        private readonly ElectLogOptions _options;

        public ElectLog(ElectLogOptions options) : base(options.BatchSize, options.Threshold)
        {
            _options = options;
        }

        public ElectLog(IOptions<ElectLogOptions> configuration) : base(configuration.Value.BatchSize,
            configuration.Value.Threshold)
        {
            _options = configuration.Value;
        }

        #region Capture

        public LogModel Capture(string message, LogType type = LogType.Error, HttpContext httpContext = null,
            string jsonFilePath = null)
        {
            var log = new LogModel(message, httpContext)
            {
                Type = type,
                JsonFilePath = jsonFilePath
            };

            return Capture(log);
        }

        public LogModel Capture(Exception exception, LogType type = LogType.Error, HttpContext httpContext = null,
            string jsonFilePath = null)
        {
            var log = new LogModel(exception, httpContext)
            {
                Type = type,
                JsonFilePath = jsonFilePath
            };

            return Capture(log);
        }

        public LogModel Capture(object obj, LogType type = LogType.Error, HttpContext httpContext = null,
            string jsonFilePath = null)
        {
            var log = new LogModel(obj, httpContext)
            {
                Type = type,
                JsonFilePath = jsonFilePath
            };

            return Capture(log);
        }

        public LogModel Capture(LogModel log)
        {
            // To Console
            if (_options.IsEnableLogToConsole)
            {
                WriteConsole(log);
            }
            
            if (_options.IsEnableLogToFile)
            {
                Push(log);
            }

            return log;
        }

        #endregion

        protected override void Execute(IEnumerable<LogModel> events)
        {
            foreach (var @event in events)
            {
                var log = @event;

                // Limit log information
                if (!_options.IsLogFullInfo)
                {
                    log.HttpContext = null;
                    log.Runtime = null;
                    log.EnvironmentModel = null;
                    log.Sdk = null;
                }

                // Before
                if (_options.BeforeLog != null)
                {
                    log = _options.BeforeLog(log);
                }

                if (log == null)
                {
                    continue;
                }

                // To File
                WriteLogToFile(log);

                // After
                if (_options.AfterLog != null)
                {
                    log = _options.AfterLog(log);
                }
            }
        }

        protected void WriteLogToFile(LogModel log)
        {
            if (!_options.IsEnableLogToFile)
            {
                return;
            }
            
            var jsonFilePath = GetJsonFilePath(_options, log);

            var isExistJsonFile = File.Exists(jsonFilePath);

            using (var store = new DataStore(jsonFilePath))
            {
                // Write Metadata first then the Logs
                if (!isExistJsonFile)
                {
                    WriteMetadata(jsonFilePath, store);
                }

                WriteLog(store, log);

                WriteMetadata(jsonFilePath, store);
            }
        }

        #region Json File Path Helper

        private static string GetJsonFilePath(ElectLogOptions options, LogModel log)
        {
            var jsonFilePath = Path.GetFullPath(!string.IsNullOrWhiteSpace(log.JsonFilePath)
                ? log.JsonFilePath
                : options.JsonFilePath);

            jsonFilePath = jsonFilePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            // Replace {Type}
            jsonFilePath = GetFilePathByType(jsonFilePath, log);

            // Replace {<DateTimeFormat>}
            var localNow = DateTimeOffset.Now;
            jsonFilePath = GetFilePathByDateTime(jsonFilePath, localNow);

            // Adjust to Json File
            if (Path.GetExtension(jsonFilePath)?.ToLowerInvariant() != ".json")
            {
                jsonFilePath = Path.ChangeExtension(jsonFilePath, ".json");
            }

            // Directory Handle
            CreateNotExistDirectory(jsonFilePath);

            return jsonFilePath;
        }

        private static string GetFilePathByType(string jsonFilePath, LogModel log)
        {
            jsonFilePath = jsonFilePath.Replace("{Type}", log.Type.ToString());
            return jsonFilePath;
        }

        private static string GetFilePathByDateTime(string jsonFilePath, DateTimeOffset dateTime)
        {
            while (true)
            {
                var iStartParam = jsonFilePath.IndexOf("{", StringComparison.Ordinal);
                var iEndParam = jsonFilePath.IndexOf("}", StringComparison.Ordinal);

                // Don't have any params
                if (iStartParam <= 0 || iEndParam <= 0 || iStartParam >= iEndParam)
                {
                    return jsonFilePath;
                }

                var length = iEndParam - iStartParam + 1; // Include last char

                var param = jsonFilePath.Substring(iStartParam, length);

                var value = dateTime.ToString(param).Trim('{', '}');

                jsonFilePath = jsonFilePath.Replace(param, value);
            }
        }

        private static void CreateNotExistDirectory(string jsonFilePath)
        {
            var jsonFolderPath = Path.GetDirectoryName(jsonFilePath);

            if (jsonFolderPath == null)
            {
                return;
            }

            if (!Directory.Exists(jsonFolderPath))
            {
                Directory.CreateDirectory(jsonFolderPath);
            }
        }

        #endregion

        #region Write helper

        private static void WriteMetadata(string jsonFilePath, IDataStore store)
        {
            var metadatas = store.GetCollection<ElectLogMetadataModel>("metadata");
            var metadata = metadatas.AsQueryable().FirstOrDefault();

            var fileInfo = new FileInfo(jsonFilePath);

            var logs = store.GetCollection<LogModel>("logs");

            var totalLog = logs.Count;

            if (metadata == null)
            {
                metadata = new ElectLogMetadataModel();
                metadata.CreatedTime = metadata.LastUpdatedTime = DateTimeOffset.Now;
                metadata.FileName = fileInfo.Name;
                metadata.FileSize = fileInfo.Length.Bytes().Humanize();
                metadata.TotalLog = totalLog;
                metadatas.InsertOne(metadata);
            }
            else
            {
                metadata.LastUpdatedTime = DateTimeOffset.Now;
                metadata.FileName = fileInfo.Name;
                metadata.FileSize = fileInfo.Length.Bytes().Humanize();
                metadata.TotalLog = totalLog;
                metadatas.UpdateOne(x => true, metadata);
            }
        }

        private static void WriteLog(IDataStore store, LogModel newLog)
        {
            newLog.JsonFilePath = null; // Force remove this information before log

            var logs = store.GetCollection<LogModel>("logs");

            logs.InsertOne(newLog);

            var logsClone = logs.AsQueryable().OrderByDescending(x => x.CreatedTime).ToList();

            logs.DeleteMany(x => true);

            logs.InsertMany(logsClone);
        }

        private static void WriteConsole(LogModel log)
        {
            string prefixText;

            ConsoleColor color = ConsoleColor.Red;
            string dateTime = DateTime.Now.ToString("h:m:s.ff tt");

            switch (log.Type)
            {
                case LogType.Debug:
                {
                    color = ConsoleColor.Gray;
                    prefixText = $"[D] [{dateTime}]";
                    break;
                }

                case LogType.Info:
                {
                    color = ConsoleColor.Cyan;
                    prefixText = $"[I] [{dateTime}]";
                    break;
                }

                case LogType.Warning:
                {
                    color = ConsoleColor.DarkYellow;
                    prefixText = $"[W] [{dateTime}]";
                    break;
                }

                case LogType.Error:
                {
                    color = ConsoleColor.Red;
                    prefixText = $"[E] [{dateTime}]";
                    break;
                }

                case LogType.Fatal:
                {
                    color = ConsoleColor.Magenta;
                    prefixText = $"[F] [{dateTime}]";
                    break;
                }

                default:
                {
                    var logType = log.Type.ToString();

                    logType = logType.Length > 4 ? logType.Substring(0, 4) : logType;

                    prefixText = $"[{logType}] [{dateTime}]";
                    break;
                }
            }

            Console.BackgroundColor = color;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(prefixText);
            Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(log.ExceptionPlace))
            {
                Console.WriteLine($" {log.ExceptionPlace}.");
            }

            Console.WriteLine($" {log.Message}.");
        }

        #endregion
    }
}