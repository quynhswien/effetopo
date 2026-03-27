using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Service for handling local file storage operations
    /// </summary>
    public class LocalStorageService
    {
        private readonly string _basePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalStorageService"/> class using the Application.AppName.
        /// </summary>
        public LocalStorageService() : this(Application.APP_NAME)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalStorageService"/> class with a custom folder name.
        /// </summary>
        /// <param name="folderName">Folder name to use for storage. If null, uses Application.AppName as fallback.</param>
        public LocalStorageService(string folderName)
        {
            // Create a folder in the user's AppData/Local directory
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string storageFolderName = !string.IsNullOrEmpty(folderName) ? folderName : Application.APP_NAME;
            _basePath = Path.Combine(appDataPath, Application.BASE_NAME, storageFolderName);

            // Ensure the directory exists
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
                Log.Information("Created local storage directory at {Path}", _basePath);
            }
        }

        /// <summary>
        /// Gets the base storage path
        /// </summary>
        public string BasePath => _basePath;

        /// <summary>
        /// Saves data to a file
        /// </summary>
        /// <typeparam name="T">Type of data to save</typeparam>
        /// <param name="fileName">Name of the file (without path)</param>
        /// <param name="data">Data to save</param>
        /// <returns>True if successful</returns>
        public async Task<bool> SaveDataAsync<T>(string fileName, T data)
        {
            try
            {
                string filePath = Path.Combine(_basePath, fileName);
                string jsonString = JsonConvert.SerializeObject(data, Formatting.Indented);

                // Using Task.Run to make synchronous File.WriteAllText work asynchronously
                await Task.Run(() => File.WriteAllText(filePath, jsonString));

                Log.Debug("Data saved to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save data to {FileName}", fileName);
                return false;
            }
        }

        /// <summary>
        /// Loads data from a file
        /// </summary>
        /// <typeparam name="T">Type of data to load</typeparam>
        /// <param name="fileName">Name of the file (without path)</param>
        /// <returns>Loaded data or default value if file doesn't exist</returns>
        public async Task<T> LoadDataAsync<T>(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_basePath, fileName);

                if (!File.Exists(filePath))
                {
                    Log.Debug("File not found: {FilePath}", filePath);
                    return default;
                }

                // Using Task.Run to make synchronous File.ReadAllText work asynchronously
                string jsonString = await Task.Run(() => File.ReadAllText(filePath));
                return JsonConvert.DeserializeObject<T>(jsonString);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load data from {FileName}", fileName);
                return default;
            }
        }

        /// <summary>
        /// Deletes a file
        /// </summary>
        /// <param name="fileName">Name of the file to delete</param>
        /// <returns>True if successful</returns>
        public bool DeleteFile(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_basePath, fileName);

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Log.Debug("Deleted file: {FilePath}", filePath);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete file {FileName}", fileName);
                return false;
            }
        }

        /// <summary>
        /// Gets the full path for a file in the storage directory
        /// </summary>
        /// <param name="fileName">File name</param>
        /// <returns>Full file path</returns>
        public string GetFilePath(string fileName)
        {
            return Path.Combine(_basePath, fileName);
        }

        /// <summary>
        /// Checks if a file exists in the storage directory
        /// </summary>
        /// <param name="fileName">File name to check</param>
        /// <returns>True if the file exists</returns>
        public bool FileExists(string fileName)
        {
            return File.Exists(Path.Combine(_basePath, fileName));
        }
    }
}