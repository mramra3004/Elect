﻿using System;
using System.IO;
using Elect.Core.Interfaces;

namespace Elect.Logger.Logging.Models
{
    public class ElectLogOptions : IElectOptions
    {
        /// <summary>
        ///     Relative Path of Json File, default is Logs.json
        /// </summary>
        public string JsonFilePath { get; set; } = "Logs" + Path.DirectorySeparatorChar + "{yyyy-MM-dd}.json";

        public uint BatchSize { get; set; } = 20;
        
        public TimeSpan ThresholdTimeSpan { get; set; } = TimeSpan.FromSeconds(2);

        public bool IsEnableLogToConsole { get; set; } = true;
        
        public bool IsEnableLogToFile { get; set; } = true;
    }
}