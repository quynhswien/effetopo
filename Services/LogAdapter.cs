using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace effetopo.Services
{
    /// <summary>
    /// Adapter class for logging functionality to work across different Revit versions and .NET frameworks.
    /// Only writes logs in DEBUG mode. In release mode, it does nothing.
    /// </summary>
    public static class Log
    {
        private static readonly bool IsDebugMode =
#if DEBUG
            true;
#else
            false;
#endif

        private static readonly string LogFilePath = GetLogFilePath();

        /// <summary>
        /// Initialize the log file path
        /// </summary>
        private static string GetLogFilePath()
        {
            if (!IsDebugMode)
                return null;

            try
            {
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                // Use hard-coded values to avoid circular dependencies
                string baseName = "EFFE";
                string appName = "effetopo";
                string logDir = Path.Combine(appDataPath, baseName, appName, "Logs");

                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                string fileName = $"log_{DateTime.Now:yyyyMMdd}.txt";
                return Path.Combine(logDir, fileName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Log debug message
        /// </summary>
        public static void Debug(string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[DEBUG] {Format(messageTemplate, propertyValues)}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log information message
        /// </summary>
        public static void Information(string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[INFO] {Format(messageTemplate, propertyValues)}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log warning message
        /// </summary>
        public static void Warning(string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[WARNING] {Format(messageTemplate, propertyValues)}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log warning message with exception
        /// </summary>
        public static void Warning(Exception exception, string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[WARNING] {Format(messageTemplate, propertyValues)}\nException: {exception?.Message}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log error message without exception
        /// </summary>
        public static void Error(string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[ERROR] {Format(messageTemplate, propertyValues)}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log error message with exception
        /// </summary>
        public static void Error(Exception exception, string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[ERROR] {Format(messageTemplate, propertyValues)}\nException: {exception?.Message}\n{exception?.StackTrace}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Log fatal error message with exception
        /// </summary>
        public static void Fatal(Exception exception, string messageTemplate, params object[] propertyValues)
        {
            if (!IsDebugMode)
                return;

            try
            {
                string message = $"[FATAL] {Format(messageTemplate, propertyValues)}\nException: {exception?.Message}\n{exception?.StackTrace}";
                WriteLog(message);
            }
            catch
            {
                // Ignore errors in logging
            }
        }

        /// <summary>
        /// Close and flush log - no-op in our implementation
        /// </summary>
        public static void CloseAndFlush()
        {
            // No-op in our implementation
        }

        /// <summary>
        /// Write log message to debug output and file if available
        /// </summary>
        private static void WriteLog(string message)
        {
            // Always write to debug output in debug mode
            System.Diagnostics.Debug.WriteLine(message);

            // Also write to file if available
            if (!string.IsNullOrEmpty(LogFilePath))
            {
                try
                {
                    string timestampedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
                    File.AppendAllText(LogFilePath, timestampedMessage + Environment.NewLine);
                }
                catch
                {
                    // Ignore file write errors
                }
            }
        }

        /// <summary>
        /// Format a message template with property values, supporting both indexed and named placeholders
        /// </summary>
        private static string Format(string messageTemplate, object[] propertyValues)
        {
            if (propertyValues == null || propertyValues.Length == 0)
                return messageTemplate;

            try
            {
                string result = messageTemplate;

                // Indexed placeholders: {0}, {0:F3}, etc.
                for (int i = 0; i < propertyValues.Length; i++)
                {
                    string pattern = @"\{" + i + @"(?::([^}]+))?\}";
                    int index = i;
                    result = Regex.Replace(result, pattern, match =>
                    {
                        object val = propertyValues[index];
                        if (val == null)
                            return "null";
                        if (match.Groups[1].Success)
                            return string.Format(CultureInfo.InvariantCulture, "{0:" + match.Groups[1].Value + "}", val);
                        return val.ToString();
                    });
                }

                // Named placeholders: {Name}, {Name:F3} — positional by appearance order.
                var namedMatches = Regex.Matches(result, @"\{([A-Za-z_][A-Za-z0-9_]*)(?::([^}]+))?\}");
                int namedIndex = 0;
                foreach (Match match in namedMatches)
                {
                    if (namedIndex >= propertyValues.Length)
                        break;

                    object val = propertyValues[namedIndex++];
                    string replacement = val == null
                        ? "null"
                        : match.Groups[2].Success
                            ? string.Format(CultureInfo.InvariantCulture, "{0:" + match.Groups[2].Value + "}", val)
                            : val.ToString();
                    result = result.Replace(match.Value, replacement);
                }

                return result;
            }
            catch
            {
                return messageTemplate;
            }
        }
    }
}