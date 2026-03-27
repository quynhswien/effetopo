using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using effetopo.Models;
using effetopo.Services;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Service for checking and handling application updates
    /// </summary>
    public class VersionCheckService
    {
        private static VersionCheckService _instance;
        private static readonly object _lock = new object();
        private const string LAST_CHECK_TIME_KEY = "last_update_check.json";

        /// <summary>
        /// Flag to track whether version has been checked already
        /// </summary>
        private static bool _versionHasBeenChecked = false;
        private static readonly object _versionCheckLock = new object();

        private readonly ManageServerService _serverService;
        private readonly LocalStorageService _localStorage;
        private DateTime _lastCheckTime = DateTime.MinValue;
        private VersionCheckResponse _cachedResponse;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static VersionCheckService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new VersionCheckService();
                        }
                    }
                }
                return _instance;
            }
        }

        private VersionCheckService()
        {
            _serverService = ManageServerService.Instance;
            _localStorage = new LocalStorageService(Application.APP_NAME);

            // Try to load the last check time from local storage
            _lastCheckTime = Task.Run(() => LoadLastCheckTimeAsync()).Result;

            Log.Debug("VersionCheckService initialized");
        }

        /// <summary>
        /// Checks for version updates if hasn't been checked yet
        /// Called by commands when they are first executed
        /// </summary>
        public static void CheckVersionIfNeeded()
        {
            // Use double-checked locking pattern to ensure it only runs once
            if (!_versionHasBeenChecked)
            {
                lock (_versionCheckLock)
                {
                    if (!_versionHasBeenChecked)
                    {
                        _versionHasBeenChecked = true;
                        Log.Information("Checking for version updates on background");
                        // Start the version check in a background task
                        BackgroundTaskService.Instance.RunBackgroundTask(async token =>
                        {
                            try
                            {
                                Log.Information("Starting version check on first command execution");

                                if (token.IsCancellationRequested)
                                {
                                    return;
                                }

                                // Check for updates
                                var versionService = VersionCheckService.Instance;
                                var updateInfo = await versionService.CheckForUpdateAsync(forceCheck: true);

                                if (token.IsCancellationRequested)
                                {
                                    return;
                                }

                                if (updateInfo != null)
                                {
                                    Log.Information("Update available: v{0}", updateInfo.VersionNumber);

                                    // Use the generic notification handler
                                    RevitNotificationHandler.Instance.ShowVersionUpdateNotification(updateInfo);
                                }
                                else
                                {
                                    Log.Information("No updates available");
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!(ex is TaskCanceledException || ex is OperationCanceledException))
                                {
                                    Log.Error(ex, "Error during version check");
                                    StatisticsCollectorService.Instance.RecordError("VersionCheckError", ex.Message, ex.StackTrace);
                                }
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Load the last check time from storage
        /// </summary>
        private async Task<DateTime> LoadLastCheckTimeAsync()
        {
            try
            {
                var lastCheckTime = await _localStorage.LoadDataAsync<DateTime?>(LAST_CHECK_TIME_KEY);
                if (lastCheckTime.HasValue)
                {
                    Log.Debug("Loaded last version check time: {0}", lastCheckTime.Value);
                    return lastCheckTime.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load last check time");
            }

            return DateTime.MinValue;
        }

        /// <summary>
        /// Checks if a newer version is available
        /// </summary>
        /// <param name="includePrerelease">Whether to include pre-release versions</param>
        /// <param name="forceCheck">Force check even if recently checked</param>
        /// <returns>Version information if update available, null otherwise</returns>
        public async Task<VersionCheckResponse> CheckForUpdateAsync(bool includePrerelease = false, bool forceCheck = false)
        {
            Log.Information("Checking for updates");

            // If we've checked in the last hour and it's not a forced check, return cached result
            if (!forceCheck && _cachedResponse != null && (DateTime.UtcNow - _lastCheckTime).TotalHours < 1)
            {
                Log.Debug("Returning cached version check result from {0}", _lastCheckTime);
                return _cachedResponse;
            }

            try
            {
                StatisticsCollectorService.Instance.RecordFeatureUsage("CheckForUpdate");

                var request = new VersionCheckRequest
                {
                    AddinCode = _serverService.AddinCode,
                    CurrentVersion = Application.APP_VERSION,
                    MachineId = _serverService.MachineId,
                    RevitVersion = Application.RevitVersionNumber,
                    IncludePrerelease = includePrerelease
                };

                string endpoint = $"{ManageServerService.ENDPOINTS["check_version"]}";
                string requestJson = JsonConvert.SerializeObject(request);
                var response = await _serverService.PostAndGetResponseAsync<VersionCheckResponse>(endpoint, requestJson);

                // Update cache
                _cachedResponse = response;
                _lastCheckTime = DateTime.UtcNow;


                // Save last check time using proper method
                await _localStorage.SaveDataAsync(LAST_CHECK_TIME_KEY, _lastCheckTime);

                if (response != null)
                {
                    // Only consider it an update if the version is different
                    if (response.VersionNumber != Application.APP_VERSION)
                    {
                        Log.Debug("Update available: {0} (current: {1})",
                            response.VersionNumber, Application.APP_VERSION);
                        return response;
                    }
                    else
                    {
                        Log.Debug("Application is already at the latest version: {0}", response.VersionNumber);
                        return null;
                    }
                }
                else
                {
                    Log.Information("No updates available");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error checking for updates");
                StatisticsCollectorService.Instance.RecordError("UpdateCheckFailed", ex.Message, ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Downloads a specific version of the addin
        /// </summary>
        /// <param name="versionId">ID of the version to download</param>
        /// <param name="autoRunAfterClose">Whether to automatically run the installer after Revit closes</param>
        /// <returns>Path to the downloaded file, or null if download failed</returns>
        public async Task<string> DownloadVersionAsync(string versionId, bool autoRunAfterClose = false)
        {
            try
            {
                StatisticsCollectorService.Instance.RecordFeatureUsage("DownloadUpdate");
                Log.Debug("Download new version {0}", versionId);

                string endpoint = $"{ManageServerService.ENDPOINTS["download_version"]}{versionId}/?machine_id={_serverService.MachineId}&revit_version={Application.RevitVersionNumber}";

                var downloadResult = await _serverService.DownloadFileAsync(endpoint);
                var fileData = downloadResult.Data;
                var fileName = downloadResult.Filename;

                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = $"Update_{versionId}.zip";
                }

                if (fileData != null && fileData.Length > 0)
                {
                    // Save the file to the downloads folder
                    string downloadsFolder = Path.Combine(_localStorage.BasePath, "Downloads");
                    Directory.CreateDirectory(downloadsFolder);

                    string filePath = Path.Combine(downloadsFolder, fileName);
#if NETCORE
                    await File.WriteAllBytesAsync(filePath, fileData);
#else
                    File.WriteAllBytes(filePath, fileData);
#endif

                    Log.Information("Update downloaded to {0}", filePath);

                    // Setup auto-run if requested
                    if (autoRunAfterClose)
                    {
                        SetupAutoRunAfterRevitCloses(filePath);
                    }

                    return filePath;
                }
                else
                {
                    Log.Warning("Download failed - empty or null response");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error downloading update");
                StatisticsCollectorService.Instance.RecordError("UpdateDownloadFailed", ex.Message, ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Sets up a script to run the installer after Revit closes
        /// </summary>
        /// <param name="installerPath">Path to the installer file</param>
        private void SetupAutoRunAfterRevitCloses(string installerPath)
        {
            try
            {
                // Get current process ID
                int currentProcessId = Process.GetCurrentProcess().Id;
                string tempDir = Path.Combine(Path.GetTempPath(), "EtagAutoUpdateRunner");
                Directory.CreateDirectory(tempDir);

                // Create a batch file that will wait for Revit to close then run the installer
                string batchFilePath = Path.Combine(tempDir, "RunAfterRevitCloses.bat");
                string batchContent =
                    $@"@echo off
echo Waiting for Revit (PID: {currentProcessId}) to close...
:WAITLOOP
timeout /t 5 /nobreak >nul
tasklist /FI ""PID eq {currentProcessId}"" | find ""{currentProcessId}"" >nul
if not errorlevel 1 goto WAITLOOP
echo Revit has closed, running installer...
start """" ""{installerPath}""
echo Cleaning up...
(goto) 2>nul & del ""%~f0""";

                File.WriteAllText(batchFilePath, batchContent);

                // Start the batch file in a separate process
                Process.Start(new ProcessStartInfo
                {
                    FileName = batchFilePath,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                Log.Information("Auto-run setup complete. Installer will run after Revit closes.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting up auto-run after Revit closes");
                StatisticsCollectorService.Instance.RecordError("AutoRunSetupFailed", ex.Message, ex.StackTrace);
            }
        }
    }
}