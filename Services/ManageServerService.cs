using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using effetopo.Services;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Central service for managing server API interactions
    /// </summary>
    public class ManageServerService
    {
        private const string BASE_SERVER_URL = "https://portal.efferevit.com/";

        // Central collection of API endpoints
        public static readonly Dictionary<string, string> ENDPOINTS = new Dictionary<string, string>
        {
            { "stats_collect", "public/insights/" },
            { "check_version", "public/check-version/" },
            { "check_license", "public/check-license/" },
            { "activate_license", "public/license/activate/" },
            { "license_heartbeat", "public/license/heartbeat/" },
            { "download_version", "public/download-version/" }
        };

        private static ManageServerService _instance;
        private static readonly object _lock = new object();

        private readonly string _baseUrl = BASE_SERVER_URL;
        private readonly string _addinCode;
        private readonly HttpClientHandler _handler;
        private string _machineId;

        // Registry key paths for storing machine ID - using HKCU only (no admin rights required)
        private const string BASE_REGISTRY_KEY = @"SOFTWARE\EFFE";
        private const string MACHINE_ID_VALUE_NAME = "MachineIdentifier";

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static ManageServerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ManageServerService();
                        }
                    }
                }
                return _instance;
            }
        }

        public string AddinCode => _addinCode;
        public string MachineId => _machineId ?? (_machineId = GetOrCreateMachineId());

        private ManageServerService()
        {
            _addinCode = Application.APP_ID;

            // Setup handler to accept self-signed certificates
            _handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            Log.Debug("ManageServerService initialized for addin code: {0}", _addinCode);
        }

        /// <summary>
        /// Generates a unique machine ID based on hardware components and stores it in the registry
        /// </summary>
        private string GetOrCreateMachineId()
        {
            try
            {
                // First, try to get the machine ID from the registry
                string existingId = GetMachineIdFromRegistry();
                if (!string.IsNullOrEmpty(existingId))
                {
                    // Verify the ID still matches the current hardware
                    if (VerifyHardwareBinding(existingId))
                    {
                        Log.Debug("Loaded and verified machine ID from registry: {Id}", existingId);
                        return existingId;
                    }
                    else
                    {
                        Log.Warning("Stored machine ID failed hardware verification. Generating new ID.");
                    }
                }

                // Use secure storage as fallback if registry is not accessible
                if (!CanAccessRegistry())
                {
                    Log.Information("Cannot access Windows Registry, falling back to secure storage");
                    return GetOrCreateMachineIdFromSecureStorage();
                }

                // Generate a new machine ID based on hardware information
                string newId = GenerateHardwareBasedMachineId();

                // Store the new ID in the registry
                if (SaveMachineIdToRegistry(newId))
                {
                    Log.Debug("Created new machine ID and saved to registry: {Id}", newId);
                }
                else
                {
                    Log.Warning("Failed to save machine ID to registry, falling back to secure storage");
                    SaveMachineIdToSecureStorage(newId);
                }

                return newId;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in GetOrCreateMachineId, falling back to secure storage");
                return GetOrCreateMachineIdFromSecureStorage();
            }
        }

        /// <summary>
        /// Check if we can access the registry
        /// </summary>
        private bool CanAccessRegistry()
        {
            try
            {
                using (var testKey = Registry.CurrentUser.OpenSubKey("SOFTWARE", true))
                {
                    return testKey != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Retrieves the machine ID from the Windows Registry (HKCU only)
        /// </summary>
        private string GetMachineIdFromRegistry()
        {
            try
            {
                // Using only HKCU (no admin rights required)
                using (var key = Registry.CurrentUser.OpenSubKey(BASE_REGISTRY_KEY))
                {
                    if (key != null)
                    {
                        var value = key.GetValue(MACHINE_ID_VALUE_NAME) as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read machine ID from registry");
            }

            return null;
        }

        /// <summary>
        /// Saves the machine ID to the Windows Registry (HKCU only)
        /// </summary>
        private bool SaveMachineIdToRegistry(string machineId)
        {
            try
            {
                // Using only HKCU (no admin rights required)
                using (var key = Registry.CurrentUser.CreateSubKey(BASE_REGISTRY_KEY))
                {
                    if (key != null)
                    {
                        key.SetValue(MACHINE_ID_VALUE_NAME, machineId);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save machine ID to registry");
            }

            return false;
        }

        /// <summary>
        /// Fallback method to use secure storage when registry access is not available
        /// </summary>
        private string GetOrCreateMachineIdFromSecureStorage()
        {
            // Use a common file name across all addins under BASE_NAME
            string idFileName = "common_machine_id";

            // Create a SecureStorageService instance using BASE_NAME
            SecureStorageService commonSecureStorage = new SecureStorageService(Application.BASE_NAME);

            // Try to load from common secure storage first
            try
            {
                if (commonSecureStorage.SecureFileExists(idFileName))
                {
                    string id = Task.Run(() => commonSecureStorage.LoadSecureDataAsync<string>(idFileName)).Result;
                    if (!string.IsNullOrEmpty(id) && VerifyHardwareBinding(id))
                    {
                        Log.Debug("Loaded and verified machine ID from secure storage: {Id}", id);
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read machine ID from secure storage");
            }

            // Generate a new hardware-bound machine ID
            string newId = GenerateHardwareBasedMachineId();

            // Save to secure storage
            SaveMachineIdToSecureStorage(newId);

            return newId;
        }

        /// <summary>
        /// Saves machine ID to secure storage
        /// </summary>
        private bool SaveMachineIdToSecureStorage(string machineId)
        {
            try
            {
                string idFileName = "common_machine_id";
                SecureStorageService commonSecureStorage = new SecureStorageService(Application.BASE_NAME);

                bool success = Task.Run(() => commonSecureStorage.SaveSecureDataAsync(idFileName, machineId)).Result;
                if (success)
                {
                    Log.Debug("Created new machine ID and saved to secure storage: {Id}", machineId);
                    return true;
                }
                else
                {
                    Log.Warning("Failed to save machine ID to secure storage");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save machine ID to secure storage");
            }

            return false;
        }

        /// <summary>
        /// Verifies that a machine ID still matches the current hardware
        /// </summary>
        private bool VerifyHardwareBinding(string storedMachineId)
        {
            try
            {
                // Extract the hardware binding components from the stored ID
                string[] parts = storedMachineId.Split('.');
                if (parts.Length != 2)
                {
                    return false; // Invalid format
                }

                string hardwareHash = parts[0];

                // Get current hardware hash for verification
                string currentHardwareFingerprint = CreateHardwareFingerprint();

                // Generate hash of the hardware fingerprint
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(currentHardwareFingerprint));
                    string currentHardwareHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);

                    // Verify the hardware hash matches
                    return string.Equals(hardwareHash, currentHardwareHash, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error verifying hardware binding");
                return false;
            }
        }

        /// <summary>
        /// Generates a unique hardware-based machine ID with hardware binding
        /// </summary>
        private string GenerateHardwareBasedMachineId()
        {
            try
            {
                // Create a unique hardware fingerprint
                string hardwareFingerprint = CreateHardwareFingerprint();

                // Generate hash of hardware fingerprint for the first part of the ID
                using (SHA256 sha = SHA256.Create())
                {
                    // First part: Hardware binding hash (16 chars)
                    byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(hardwareFingerprint));
                    string hardwareHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);

                    // Second part: Unique identifier (16 chars)
                    string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 16);

                    // Combine them with a separator
                    return $"{hardwareHash}.{uniqueId}";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating hardware-based machine ID, falling back to random ID");
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>
        /// Creates a fingerprint based on hardware components that are difficult to spoof
        /// </summary>
        private string CreateHardwareFingerprint()
        {
            StringBuilder sb = new StringBuilder();

            try
            {
                // Get CPU ID
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        sb.Append(mo["ProcessorId"]?.ToString() ?? "");
                        break; // Just use the first processor
                    }
                }

                // Get BIOS serial
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        sb.Append(mo["SerialNumber"]?.ToString() ?? "");
                        break;
                    }
                }

                // Get baseboard serial
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        sb.Append(mo["SerialNumber"]?.ToString() ?? "");
                        break;
                    }
                }

                // Get disk serial for system drive
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        sb.Append(mo["SerialNumber"]?.ToString() ?? "");
                        break; // Just use the first disk
                    }
                }

                // Get MAC addresses of network adapters
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
                {
                    var macAddresses = new List<string>();
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string mac = mo["MACAddress"]?.ToString();
                        if (!string.IsNullOrEmpty(mac))
                        {
                            macAddresses.Add(mac);
                        }
                    }

                    // Sort for consistency and take up to 2 MAC addresses
                    macAddresses.Sort();
                    foreach (var mac in macAddresses.Take(2))
                    {
                        sb.Append(mac);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error accessing hardware information");
                // Include fallback data
                sb.Append(Environment.MachineName);
                sb.Append(Environment.UserName);
                sb.Append(Environment.OSVersion.ToString());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets the machine's name for displaying in license activations
        /// </summary>
        /// <returns>A human-friendly machine name</returns>
        public string GetMachineName()
        {
            try
            {
                return Environment.MachineName;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to get machine name");
                return "Unknown Machine";
            }
        }

        /// <summary>
        /// Sends data to the specified API endpoint
        /// </summary>
        /// <param name="endpoint">API endpoint path</param>
        /// <param name="jsonPayload">JSON data to send</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> PostToServerAsync(string endpoint, string jsonPayload)
        {
            // Create a new handler for each request to avoid disposal issues
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using (var client = new HttpClient(handler))
            {
                try
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    client.BaseAddress = new Uri(_baseUrl);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(endpoint, content);

                    bool success = response.IsSuccessStatusCode;
                    if (success)
                    {
                        Log.Information("POST to {Endpoint} successful", endpoint);
                    }
                    else
                    {
                        Log.Warning("Failed to POST to {Endpoint}: HTTP {StatusCode}", endpoint, response.StatusCode);
                    }

                    return success;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to connect to server at {Endpoint}", endpoint);
                    return false;
                }
            }
        }

        /// <summary>
        /// Sends a POST request to the server and returns the response content
        /// </summary>
        /// <typeparam name="TResponse">Type to deserialize the response to</typeparam>
        /// <param name="endpoint">API endpoint path</param>
        /// <param name="jsonPayload">JSON data to send</param>
        /// <returns>Deserialized response object or default if request failed</returns>
        public async Task<TResponse> PostAndGetResponseAsync<TResponse>(string endpoint, string jsonPayload)
        {
            // Create a new handler for each request to avoid disposal issues
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using (var client = new HttpClient(handler))
            {
                try
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    client.BaseAddress = new Uri(_baseUrl);

                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(endpoint, content);

                    if (response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                        {
                            return default;
                        }

                        string responseContent = await response.Content.ReadAsStringAsync();
                        Log.Debug("Received response from {0}: {1}", endpoint, responseContent);

                        return JsonConvert.DeserializeObject<TResponse>(responseContent);
                    }
                    else
                    {
                        Log.Warning("Failed to POST to {0}: HTTP {1}", endpoint, response.StatusCode);
                        return default;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to connect to server at {0}", endpoint);
                    return default;
                }
            }
        }

        /// <summary>
        /// Downloads a file from the specified URL
        /// </summary>
        /// <param name="endpoint">API endpoint path</param>
        /// <returns>Downloaded file as byte array and filename, or null if download failed</returns>
        public async Task<(byte[] Data, string Filename)> DownloadFileAsync(string endpoint)
        {
            // Create a new handler for each download to avoid disposal issues
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using (var client = new HttpClient(handler))
            {
                try
                {
                    client.Timeout = TimeSpan.FromMinutes(5); // Larger timeout for file downloads
                    client.BaseAddress = new Uri(_baseUrl);

                    // Use GetAsync to access headers
                    Log.Debug("Starting file download from {Endpoint}", endpoint);
                    var response = await client.GetAsync(endpoint);

                    if (response.IsSuccessStatusCode)
                    {
                        // Try to get filename from Content-Disposition header
                        string filename = "update.zip";
                        if (response.Content.Headers.ContentDisposition != null)
                        {
                            string contentDisposition = response.Content.Headers.ContentDisposition.FileName;
                            if (!string.IsNullOrEmpty(contentDisposition))
                            {
                                filename = contentDisposition.Trim('"');
                                Log.Debug("Detected filename from Content-Disposition: {Filename}", filename);
                            }
                        }

                        byte[] data = await response.Content.ReadAsByteArrayAsync();
                        Log.Information("File download from {Endpoint} successful, received {Size} bytes with filename {Filename}",
                            endpoint, data.Length, filename);

                        return (data, filename);
                    }
                    else
                    {
                        Log.Warning("Failed to download file from {Endpoint}: HTTP {StatusCode}", endpoint, response.StatusCode);
                        return (null, null);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to download file from {Endpoint}", endpoint);
                    return (null, null);
                }
            }
        }
    }
}