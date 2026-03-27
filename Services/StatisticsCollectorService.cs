using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Service for collecting and submitting usage statistics
    /// </summary>
    public class StatisticsCollectorService
    {
        private static StatisticsCollectorService _instance;
        private static readonly object _lock = new object();

        private readonly ManageServerService _serverService;
        private readonly string _endpoint = ManageServerService.ENDPOINTS["stats_collect"];
        private readonly string _addinCode;
        private readonly System.Timers.Timer _uploadTimer;
        private readonly List<SessionEvent> _sessionEvents = new List<SessionEvent>();
        private readonly List<FeatureUsage> _featureUsage = new List<FeatureUsage>();
        private readonly List<ErrorEvent> _errorEvents = new List<ErrorEvent>();
        private readonly LocalStorageService _localStorage;
        private readonly LocalStorageService _statsStorage;
        private string _currentSessionId;
        private DateTime _sessionStartTime;
        private const string STATS_SUBFOLDER = "Stats";

        /// <summary>
        /// Default instance for backward compatibility
        /// </summary>
        public static StatisticsCollectorService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new StatisticsCollectorService();
                        }
                    }
                }
                return _instance;
            }
        }

        private StatisticsCollectorService()
        {
            // Get reference to the central server service
            _serverService = ManageServerService.Instance;
            _addinCode = _serverService.AddinCode;

            // Create main storage service
            _localStorage = new LocalStorageService(Application.APP_NAME);

            // Create a dedicated storage service for stats subfolder
            _statsStorage = new LocalStorageService(Path.Combine(Application.APP_NAME, STATS_SUBFOLDER));

            _currentSessionId = Guid.NewGuid().ToString();
            _sessionStartTime = DateTime.UtcNow;

            // Setup timer to upload stats every 30 minutes
            _uploadTimer = new System.Timers.Timer(30 * 60 * 1000); // 30 minutes
            _uploadTimer.Elapsed += async (s, e) => await UploadStatsAsync();
            _uploadTimer.Start();

            Log.Debug("Statistics collector initialized for addin code: {AddinCode}", _addinCode);
        }

        /// <summary>
        /// Records the start of a user session
        /// </summary>
        public void RecordSessionStart()
        {
            _currentSessionId = Guid.NewGuid().ToString();
            _sessionStartTime = DateTime.UtcNow;

            var sessionEvent = new SessionEvent
            {
                EventType = "start",
                Timestamp = _sessionStartTime.ToString("o"),
                SessionId = _currentSessionId,
                AdditionalData = null
            };

            lock (_lock)
            {
                _sessionEvents.Add(sessionEvent);
            }

            Log.Debug("Session started: {SessionId}", _currentSessionId);
        }

        /// <summary>
        /// Records the end of a user session
        /// </summary>
        public void RecordSessionEnd()
        {
            var endTime = DateTime.UtcNow;
            var duration = (int)(endTime - _sessionStartTime).TotalSeconds;

            var sessionEvent = new SessionEvent
            {
                EventType = "end",
                Timestamp = endTime.ToString("o"),
                SessionId = _currentSessionId,
                Duration = duration,
                AdditionalData = null
            };

            lock (_lock)
            {
                _sessionEvents.Add(sessionEvent);
            }

            Log.Debug("Session ended: {SessionId}, Duration: {Duration} seconds", _currentSessionId, duration);

            // Upload stats when session ends
            Task.Run(async () => await UploadStatsAsync());
        }

        /// <summary>
        /// Records the usage of a feature
        /// </summary>
        /// <param name="featureName">Name of the feature used</param>
        /// <param name="count">Number of times the feature was used</param>
        /// <param name="additionalData">Additional data to record</param>
        public void RecordFeatureUsage(string featureName, int count = 1, Dictionary<string, object> additionalData = null)
        {
            var featureUsage = new FeatureUsage
            {
                SessionId = _currentSessionId,
                FeatureName = featureName,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Count = count,
                Duration = null,
                AdditionalData = additionalData
            };

            lock (_lock)
            {
                _featureUsage.Add(featureUsage);
            }

            Log.Debug("Feature usage recorded: {FeatureName}, Count: {Count}", featureName, count);
        }

        /// <summary>
        /// Records an error that occurred during execution
        /// </summary>
        /// <param name="errorType">Type of error</param>
        /// <param name="errorMessage">Error message</param>
        /// <param name="stackTrace">Stack trace of the error</param>
        /// <param name="additionalData">Additional data to record</param>
        public void RecordError(string errorType, string errorMessage, string stackTrace, Dictionary<string, object> additionalData = null)
        {
            var errorEvent = new ErrorEvent
            {
                SessionId = _currentSessionId,
                ErrorType = errorType,
                ErrorMessage = errorMessage,
                StackTrace = stackTrace,
                Timestamp = DateTime.UtcNow.ToString("o"),
                AdditionalData = additionalData
            };

            lock (_lock)
            {
                _errorEvents.Add(errorEvent);
            }

            Log.Error("Error recorded: {ErrorType}, {ErrorMessage}", errorType, errorMessage);
        }

        /// <summary>
        /// Uploads collected statistics to the server
        /// </summary>
        public async Task UploadStatsAsync()
        {
            try
            {
                // Make a copy of the current stats and clear the lists
                List<SessionEvent> sessionEvents;
                List<FeatureUsage> featureUsage;
                List<ErrorEvent> errorEvents;

                lock (_lock)
                {
                    sessionEvents = new List<SessionEvent>(_sessionEvents);
                    featureUsage = new List<FeatureUsage>(_featureUsage);
                    errorEvents = new List<ErrorEvent>(_errorEvents);

                    _sessionEvents.Clear();
                    _featureUsage.Clear();
                    _errorEvents.Clear();
                }

                // Skip if no data to upload
                if (sessionEvents.Count == 0 && featureUsage.Count == 0 && errorEvents.Count == 0)
                {
                    return;
                }

                // Create stats payload
                var stats = new StatsPayload
                {
                    AddinCode = _addinCode,
                    AddinInstance = new AddinInstance
                    {
                        MachineId = _serverService.MachineId,
                        DeviceName = Environment.MachineName,
                        OperatingSystem = Environment.OSVersion.Platform.ToString(),
                        OsVersion = Environment.OSVersion.Version.ToString(),
                        AppVersion = Application.APP_VERSION,
                        IsActive = true
                    },
                    SessionEvents = sessionEvents,
                    FeatureUsage = featureUsage,
                    ErrorEvents = errorEvents
                };

                // Convert to JSON
                string jsonPayload = JsonConvert.SerializeObject(stats);

                // Try to upload using the central server service
                bool uploadSuccess = await _serverService.PostToServerAsync(_endpoint, jsonPayload);

                // If upload fails, store locally
                if (!uploadSuccess)
                {
                    await SaveLocallyAsync(jsonPayload);
                    await TryUploadPendingDataAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error uploading stats");
                // Don't try to record this error to avoid potential infinite loops
            }
        }

        /// <summary>
        /// Stores current statistics for later upload
        /// </summary>
        private async Task StoreStatsForLaterAsync()
        {
            try
            {
                List<SessionEvent> sessionEvents;
                List<FeatureUsage> featureUsage;
                List<ErrorEvent> errorEvents;

                lock (_lock)
                {
                    // Skip if no data to store
                    if (_sessionEvents.Count == 0 && _featureUsage.Count == 0 && _errorEvents.Count == 0)
                    {
                        return;
                    }

                    sessionEvents = new List<SessionEvent>(_sessionEvents);
                    featureUsage = new List<FeatureUsage>(_featureUsage);
                    errorEvents = new List<ErrorEvent>(_errorEvents);

                    _sessionEvents.Clear();
                    _featureUsage.Clear();
                    _errorEvents.Clear();
                }

                // Create stats payload
                var stats = new StatsPayload
                {
                    AddinCode = _addinCode,
                    AddinInstance = new AddinInstance
                    {
                        MachineId = _serverService.MachineId,
                        DeviceName = Environment.MachineName,
                        OperatingSystem = Environment.OSVersion.Platform.ToString(),
                        OsVersion = Environment.OSVersion.Version.ToString(),
                        AppVersion = Application.APP_VERSION,
                        IsActive = true
                    },
                    SessionEvents = sessionEvents,
                    FeatureUsage = featureUsage,
                    ErrorEvents = errorEvents
                };

                // Convert to JSON and save
                string jsonPayload = JsonConvert.SerializeObject(stats);
                await SaveLocallyAsync(jsonPayload);

                Log.Debug("Statistics stored locally for later upload");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error storing statistics for later upload");
            }
        }

        private async Task SaveLocallyAsync(string jsonPayload)
        {
            try
            {
                string filename = $"stats_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid()}.json";

                // Save using the stats-specific storage service
                bool success = await _statsStorage.SaveDataAsync(filename, jsonPayload);

                if (success)
                {
                    Log.Debug("Statistics saved locally to {FileName}", filename);
                }
                else
                {
                    Log.Warning("Failed to save statistics locally");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save stats locally");
            }
        }

        private async Task TryUploadPendingDataAsync()
        {
            try
            {
                // Get all statistics files from the stats folder
                string statsDirectory = _statsStorage.BasePath;

                // Ensure directory exists
                if (!Directory.Exists(statsDirectory))
                {
                    return;
                }

                // Find all stat files in the directory
                var directory = new DirectoryInfo(statsDirectory);
                var statsFiles = directory.GetFiles("stats_*.json");

                int successCount = 0;
                foreach (var file in statsFiles)
                {
                    try
                    {
                        // Extract just the filename without path
                        string fileName = Path.GetFileName(file.Name);

                        // Load the file content using LocalStorageService
                        string jsonPayload = await _statsStorage.LoadDataAsync<string>(fileName);

                        if (jsonPayload != null)
                        {
                            // Use the central server service to send the pending data
                            bool success = await _serverService.PostToServerAsync(_endpoint, jsonPayload);

                            if (success)
                            {
                                // Delete the file using LocalStorageService
                                _statsStorage.DeleteFile(fileName);
                                successCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error processing pending file {FileName}", file.Name);
                        // Skip this file
                    }
                }

                if (successCount > 0)
                {
                    Log.Information("Uploaded {Count} pending statistics files", successCount);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error uploading pending data");
            }
        }

        /// <summary>
        /// Shuts down the statistics collector, stopping the upload timer and recording session end
        /// </summary>
        public void Shutdown()
        {
            _uploadTimer.Stop();
            RecordSessionEnd();
            Log.Debug("Statistics collector shut down");
        }
    }
}
