using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Service for verifying and managing licenses
    /// </summary>
    public class LicenseCheckService
    {
        private static LicenseCheckService _instance;
        private static readonly object _lock = new object();

        private readonly ManageServerService _serverService;
        private readonly LocalStorageService _localStorage;
        private readonly SecureStorageService _secureStorage;
        private DateTime _lastCheckTime = DateTime.MinValue;
        public LicenseCheckResponse CachedResponse;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static LicenseCheckService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new LicenseCheckService();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Whether the current user is licensed
        /// </summary>
        public bool IsLicensed => CachedResponse?.IsLicensed ?? false;

        /// <summary>
        /// License type if licensed (subscription, lifetime, organization)
        /// </summary>
        public string LicenseType => CachedResponse?.LicenseType;

        /// <summary>
        /// Expiry date of the license, if applicable
        /// </summary>
        public DateTime? ExpiryDate => CachedResponse?.ExpiryDate;

        /// <summary>
        /// Status of the license (active, expired, etc.)
        /// </summary>
        public string Status => CachedResponse?.Status;

        /// <summary>
        /// Message about the license status
        /// </summary>
        public string Message => CachedResponse?.Message;

        /// <summary>
        /// Time of the last license check
        /// </summary>
        public DateTime LastCheckTime => _lastCheckTime;

        /// <summary>
        /// Determines if a license refresh is needed
        /// </summary>
        /// <param name="hourThreshold">Hours since last check to trigger refresh</param>
        /// <returns>True if refresh is needed</returns>
        public bool ShouldRefreshLicense(double hourThreshold = 1.0)
        {
            return CachedResponse == null || (DateTime.UtcNow - _lastCheckTime).TotalHours >= hourThreshold;
        }

        private LicenseCheckService()
        {
            _serverService = ManageServerService.Instance;
            _localStorage = new LocalStorageService(Application.APP_NAME);
            _secureStorage = new SecureStorageService(Application.APP_NAME);

            Log.Debug("LicenseCheckService initialized");
        }

        /// <summary>
        /// Checks license status with the server and updates application state
        /// </summary>
        /// <param name="forceCheck">Whether to force a check even if cached data exists</param>
        /// <param name="token">Cancellation token to cancel the operation</param>
        /// <returns>True if the license is valid, false otherwise</returns>
        public async Task<bool> CheckAndUpdateLicenseAsync(bool forceCheck = false, CancellationToken token = default)
        {
            try
            {
                Log.Information("Checking license");
                var licenseInfo = await CheckLicenseAsync(forceCheck);

                if (token.IsCancellationRequested)
                {
                    return Application.IsLicensed;
                }

                UpdateApplicationLicenseStatus(licenseInfo);
                return Application.IsLicensed;
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    // Just return current license status if canceled
                    return Application.IsLicensed;
                }

                return HandleLicenseCheckError(ex);
            }
        }

        /// <summary>
        /// Handles errors during license checking by applying appropriate fallback strategies
        /// </summary>
        /// <param name="ex">The exception that occurred</param>
        /// <returns>True if license is considered valid in fallback mode, false otherwise</returns>
        private bool HandleLicenseCheckError(Exception ex)
        {
            string appLicenseRequirement = Application.LICENSE_REQUIREMENT.ToLower();

            if (appLicenseRequirement == "none")
            {
                // For "none" requirement, allow operation when errors occur
                Application.IsLicensed = true;
                Application.LicenseStatusMessage = "Error during license check. Running with limited features.";
                Log.Error(ex, "Error checking license, allowing operation based on LICENSE_REQUIREMENT");
                return true;
            }
            else
            {
                Application.IsLicensed = false;
                Application.LicenseStatusMessage = $"Error checking license: {ex.Message}";
                Log.Error(ex, "Error checking license");
                return false;
            }
        }

        /// <summary>
        /// Updates the application's license status based on the response from the server
        /// </summary>
        /// <param name="licenseInfo">The license check response from the server</param>
        private void UpdateApplicationLicenseStatus(LicenseCheckResponse licenseInfo)
        {
            if (licenseInfo == null)
            {
                ApplyOfflineLicensingStrategy();
            }
            else if (licenseInfo.IsLicensed)
            {
                // License is valid
                Application.IsLicensed = true;
                Application.LicenseStatusMessage = $"Licensed ({licenseInfo.LicenseType})";
                if (licenseInfo.ExpiryDate.HasValue)
                {
                    Application.LicenseStatusMessage += $", expires: {licenseInfo.ExpiryDate.Value.ToShortDateString()}";
                }
                Log.Information("License valid: {Type}, expires: {ExpiryDate}",
                    licenseInfo.LicenseType, licenseInfo.ExpiryDate);
            }
            else
            {
                // No valid license
                Application.IsLicensed = false;
                Application.LicenseStatusMessage = licenseInfo.Message;
                Log.Warning("License invalid: {Message}", licenseInfo.Message);
            }
        }

        /// <summary>
        /// Applies the appropriate licensing strategy when in offline mode
        /// </summary>
        private void ApplyOfflineLicensingStrategy()
        {
            // In case of offline mode with no cached response, use the application default license requirement
            string appLicenseRequirement = Application.LICENSE_REQUIREMENT.ToLower();
            if (appLicenseRequirement == "none" || appLicenseRequirement == "warn")
            {
                // For "none" or "warn" requirement, allow operation when offline
                Application.IsLicensed = true;
                Application.LicenseStatusMessage = "Running in offline mode. Some features may be limited.";
                Log.Warning("License check failed - no response from server, running in offline mode");
            }
            else
            {
                // For "enforce" requirement, block operation when offline
                Application.IsLicensed = false;
                Application.LicenseStatusMessage = "Could not verify license. Please check your internet connection.";
                Log.Warning("License check failed - no response from server");
            }
        }

        /// <summary>
        /// Checks if this machine has a valid license
        /// </summary>
        /// <param name="forceCheck">Force check even if recently checked</param>
        /// <returns>License information</returns>
        public async Task<LicenseCheckResponse> CheckLicenseAsync(bool forceCheck = false)
        {
            // If we've checked in the last hour and it's not a forced check, return cached result
            if (!forceCheck && CachedResponse != null && (DateTime.UtcNow - _lastCheckTime).TotalHours < 1)
            {
                Log.Debug("Returning cached license check result from {Time}", _lastCheckTime);
                return CachedResponse;
            }

            try
            {
                StatisticsCollectorService.Instance.RecordFeatureUsage("CheckLicense");

                var request = new LicenseCheckRequest
                {
                    MachineId = _serverService.MachineId,
                    ProductCode = _serverService.AddinCode,
                    RevitVersion = Application.RevitVersionNumber
                };

                string endpoint = ManageServerService.ENDPOINTS["check_license"];
                string requestJson = JsonConvert.SerializeObject(request);

                var response = await _serverService.PostAndGetResponseAsync<LicenseCheckResponse>(endpoint, requestJson);

                // Update cache if response is valid
                if (response != null)
                {
                    CachedResponse = response;
                    _lastCheckTime = DateTime.UtcNow;

                    if (response.IsLicensed)
                    {
                        Log.Information("License verified for machine {MachineId}, type: {Type}, expires: {ExpiryDate}",
                            _serverService.MachineId, response.LicenseType, response.ExpiryDate);
                    }
                    else
                    {
                        Log.Warning("License check failed for machine {MachineId}: {Message}",
                            _serverService.MachineId, response.Message);
                    }
                }
                else
                {
                    Log.Warning("License check returned no response for machine {MachineId}", _serverService.MachineId);
                }

                return response;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error checking license for machine {MachineId}", _serverService.MachineId);
                StatisticsCollectorService.Instance.RecordError("LicenseCheckFailed", ex.Message, ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Activates a license for this machine
        /// </summary>
        /// <param name="licenseKey">License key to activate</param>
        /// <returns>Activation response</returns>
        public async Task<LicenseActivationResponse> ActivateLicenseAsync(string licenseKey)
        {
            if (string.IsNullOrEmpty(licenseKey))
            {
                Log.Warning("Cannot activate license with empty license key");
                return null;
            }

            try
            {
                StatisticsCollectorService.Instance.RecordFeatureUsage("ActivateLicense");

                var request = new LicenseActivationRequest
                {
                    LicenseKey = licenseKey,
                    MachineId = _serverService.MachineId,
                    MachineName = _serverService.GetMachineName(),
                    ProductCode = _serverService.AddinCode
                };

                string endpoint = ManageServerService.ENDPOINTS["activate_license"];
                string requestJson = JsonConvert.SerializeObject(request);

                var response = await _serverService.PostAndGetResponseAsync<LicenseActivationResponse>(endpoint, requestJson);

                if (response != null && response.Status == "success")
                {
                    // Force a fresh license check to update cached response
                    bool isLicensed = await CheckAndUpdateLicenseAsync(forceCheck: true);
                    if (isLicensed)
                    {
                        Log.Information("License activated successfully for machine {MachineId}", _serverService.MachineId);
                    }
                    else
                    {
                        Log.Warning("License activation failed for machine {MachineId}", _serverService.MachineId);
                        return null;
                    }
                }
                else
                {
                    string errorMessage = response?.Error ?? "Unknown error";
                    Log.Warning("License activation failed: {Error}", errorMessage);
                }

                return response;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error activating license for machine {MachineId}", _serverService.MachineId);
                StatisticsCollectorService.Instance.RecordError("LicenseActivationFailed", ex.Message, ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Sends a heartbeat to the license server to confirm the license is still in use
        /// </summary>
        /// <returns>True if heartbeat was successful, false otherwise</returns>
        public async Task<bool> SendLicenseHeartbeatAsync()
        {
            try
            {
                var request = new LicenseHeartbeatRequest
                {
                    MachineId = _serverService.MachineId
                };

                string endpoint = ManageServerService.ENDPOINTS["license_heartbeat"];
                string requestJson = JsonConvert.SerializeObject(request);

                var response = await _serverService.PostAndGetResponseAsync<LicenseHeartbeatResponse>(endpoint, requestJson);

                if (response != null && response.Status == "success")
                {
                    Log.Debug("License heartbeat successful for machine {MachineId}", _serverService.MachineId);

                    // Update expiry date if provided
                    if (CachedResponse != null && response.ExpiryDate.HasValue)
                    {
                        CachedResponse.ExpiryDate = response.ExpiryDate;
                    }

                    return true;
                }
                else
                {
                    Log.Warning("License heartbeat failed for machine {MachineId}", _serverService.MachineId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error sending license heartbeat for machine {MachineId}", _serverService.MachineId);
                return false;
            }
        }

        /// <summary>
        /// Clears the cached license information
        /// </summary>
        public void ClearLicenseCache()
        {
            CachedResponse = null;
            _lastCheckTime = DateTime.MinValue;
            Log.Debug("License cache cleared");
        }
    }
}