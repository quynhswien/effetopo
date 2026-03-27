using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace effetopo.Services
{
    /// <summary>
    /// Service for securely storing and retrieving sensitive data using Windows DPAPI
    /// </summary>
    public class SecureStorageService
    {
        private readonly string _appDataPath;
        private readonly string _encryptedDirectory;

        /// <summary>
        /// Initialize secure storage service
        /// </summary>
        /// <param name="applicationName">Application name or BASE_NAME for common storage</param>
        public SecureStorageService(string applicationName)
        {
            // Create a secure storage location in AppData
            string appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (applicationName == Application.BASE_NAME)
            {
                // If using BASE_NAME directly, create common storage for all addins
                _appDataPath = Path.Combine(appDataRoot, applicationName);
                _encryptedDirectory = Path.Combine(_appDataPath, "Common", "Secure");
            }
            else
            {
                // Regular app-specific storage
                string _basePath = Path.Combine(appDataRoot, Application.BASE_NAME);
                _appDataPath = Path.Combine(_basePath, applicationName);
                _encryptedDirectory = Path.Combine(_appDataPath, "Secure");
            }

            // Ensure directories exist
            Directory.CreateDirectory(_appDataPath);
            Directory.CreateDirectory(_encryptedDirectory);

            // Set restrictive permissions on the secure directory
            // try
            // {
            //     var dirInfo = new DirectoryInfo(_encryptedDirectory);
            //     var security = dirInfo.GetAccessControl();
            //     security.SetAccessRuleProtection(true, false); // Disable inheritance
            //     dirInfo.SetAccessControl(security);
            // }
            // catch (Exception ex)
            // {
            //     Log.Warning(ex, "Could not set restrictive permissions on secure directory");
            // }
        }

        /// <summary>
        /// Securely saves data by encrypting it with DPAPI
        /// </summary>
        /// <param name="fileName">Name of the file to save to</param>
        /// <param name="data">Data to encrypt and save</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> SaveSecureDataAsync<T>(string fileName, T data)
        {
            try
            {
                string serializedData = data.ToString();
                byte[] dataBytes = Encoding.UTF8.GetBytes(serializedData);

                // Encrypt the data using DPAPI (CurrentUser scope)
                byte[] encryptedData = ProtectedData.Protect(
                    dataBytes,
                    null, // Optional entropy
                    DataProtectionScope.CurrentUser);

                string filePath = Path.Combine(_encryptedDirectory, fileName);

                // Write encrypted data to file
                await Task.Run(() => File.WriteAllBytes(filePath, encryptedData));

                Log.Debug("Data securely saved to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to securely save data to {FileName}", fileName);
                return false;
            }
        }

        /// <summary>
        /// Loads and decrypts data that was secured with DPAPI
        /// </summary>
        /// <param name="fileName">Name of the file to load from</param>
        /// <returns>Decrypted data or default if file doesn't exist or decryption fails</returns>
        public async Task<T> LoadSecureDataAsync<T>(string fileName)
        {
            string filePath = Path.Combine(_encryptedDirectory, fileName);

            if (!File.Exists(filePath))
            {
                Log.Debug("Secure file {FilePath} not found", filePath);
                return default;
            }

            try
            {
                // Read encrypted data from file
                byte[] encryptedData = await Task.Run(() => File.ReadAllBytes(filePath));

                // Decrypt the data using DPAPI (CurrentUser scope)
                byte[] decryptedData = ProtectedData.Unprotect(
                    encryptedData,
                    null, // Optional entropy
                    DataProtectionScope.CurrentUser);

                string decryptedString = Encoding.UTF8.GetString(decryptedData);

                // Convert string back to original type
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)decryptedString;
                }
                else if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
                {
                    return (T)(object)int.Parse(decryptedString);
                }
                else if (typeof(T) == typeof(Guid))
                {
                    return (T)(object)Guid.Parse(decryptedString);
                }

                Log.Debug("Data securely loaded from {FilePath}", filePath);
                return (T)Convert.ChangeType(decryptedString, typeof(T));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to securely load data from {FileName}", fileName);
                return default;
            }
        }

        /// <summary>
        /// Checks if a secure file exists
        /// </summary>
        /// <param name="fileName">Name of the file to check</param>
        /// <returns>True if the file exists, false otherwise</returns>
        public bool SecureFileExists(string fileName)
        {
            string filePath = Path.Combine(_encryptedDirectory, fileName);
            return File.Exists(filePath);
        }

        /// <summary>
        /// Deletes a secure file if it exists
        /// </summary>
        /// <param name="fileName">Name of the file to delete</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> DeleteSecureDataAsync(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_encryptedDirectory, fileName);

                if (File.Exists(filePath))
                {
                    await Task.Run(() => File.Delete(filePath));
                    Log.Debug("Secure file {FilePath} deleted", filePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete secure file {FileName}", fileName);
                return false;
            }
        }
    }
}