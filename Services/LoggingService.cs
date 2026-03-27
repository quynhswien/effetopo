using System;
using System.IO;
using System.Timers;

namespace effetopo.Services
{
    /// <summary>
    /// Service for managing logging across the application.
    /// Uses a custom Log adapter that only logs in DEBUG mode.
    /// </summary>
    public static class LoggingService
    {
        private static System.Timers.Timer _flushTimer;
        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Initializes the logger with standard configuration
        /// </summary>
        /// <param name="forceReinitialization">Whether to force reinitialization even if already initialized</param>
        public static void InitializeLogger(bool forceReinitialization = false)
        {
            // Use double-checked locking to prevent concurrent initialization
            if (!_isInitialized || forceReinitialization)
            {
                lock (_initLock)
                {
                    if (!_isInitialized || forceReinitialization)
                    {
                        ConfigureLogger();
                        _isInitialized = true;
                    }
                }
            }
        }

        /// <summary>
        /// Ensures the logger is properly initialized in the current context
        /// </summary>
        public static void EnsureInitialized()
        {
            // Check if logger is uninitialized
            if (!_isInitialized)
            {
                InitializeLogger(true);
            }
        }

        /// <summary>
        /// Configures the logger with standard settings
        /// </summary>
        private static void ConfigureLogger()
        {
#if DEBUG
            // Set up timer for periodic log flushing in DEBUG mode
            SetupPeriodicFlushing();
#endif

            Log.Information("Logging initialized.");

            // Set up unhandled exception logging
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var exception = (Exception)args.ExceptionObject;
                Log.Fatal(exception, "Domain unhandled exception");
                Log.CloseAndFlush(); // Ensure logs are written on crash
            };
        }

        /// <summary>
        /// Sets up a timer to periodically flush logs to disk
        /// </summary>
        private static void SetupPeriodicFlushing()
        {
            // Dispose existing timer if it exists
            _flushTimer?.Dispose();

            // Create a new timer for periodic flushing (30 seconds)
            _flushTimer = new System.Timers.Timer(30000);
            _flushTimer.AutoReset = true;
            _flushTimer.Elapsed += (sender, e) => Log.CloseAndFlush();
            _flushTimer.Start();
        }

        /// <summary>
        /// Explicitly flushes any pending logs to disk
        /// </summary>
        public static void FlushLogs()
        {
            Log.CloseAndFlush();
        }

        /// <summary>
        /// Shuts down the logging system
        /// </summary>
        public static void Shutdown()
        {
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
            _flushTimer = null;

            Log.CloseAndFlush();
            _isInitialized = false;
        }
    }
}