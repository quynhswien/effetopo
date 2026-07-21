using System;
using System.IO;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Loads and saves Set Elevation dialog settings under %LocalAppData%\EFFE\effetopo.
    /// </summary>
    public class SetElevationSettingsService
    {
        private const string SettingsFileName = "set_elevation.json";

        private static SetElevationSettingsService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static SetElevationSettingsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SetElevationSettingsService();
                    }
                }
                return _instance;
            }
        }

        private SetElevationSettingsService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public SetElevationSettings Load()
        {
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                if (!File.Exists(path))
                    return new SetElevationSettings();

                string json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<SetElevationSettings>(json);
                return settings ?? new SetElevationSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load set elevation settings");
                return new SetElevationSettings();
            }
        }

        public void Save(SetElevationSettings settings)
        {
            if (settings == null) return;
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save set elevation settings");
            }
        }
    }
}
