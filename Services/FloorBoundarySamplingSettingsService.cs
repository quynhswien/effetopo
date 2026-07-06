using System;
using System.IO;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Loads and saves Boundary Point Division dialog settings under %LocalAppData%\EFFE\effetopo.
    /// </summary>
    public class FloorBoundarySamplingSettingsService
    {
        private const string SettingsFileName = "floor_boundary_sampling.json";

        private static FloorBoundarySamplingSettingsService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static FloorBoundarySamplingSettingsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new FloorBoundarySamplingSettingsService();
                    }
                }
                return _instance;
            }
        }

        private FloorBoundarySamplingSettingsService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public FloorBoundarySamplingSettings Load()
        {
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                if (!File.Exists(path))
                    return new FloorBoundarySamplingSettings();

                string json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<FloorBoundarySamplingSettings>(json);
                if (settings == null)
                    return new FloorBoundarySamplingSettings();

                Log.Debug("Loaded floor boundary sampling settings from {Path}", path);
                return settings;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load floor boundary sampling settings");
                return new FloorBoundarySamplingSettings();
            }
        }

        public void Save(FloorBoundarySamplingSettings settings)
        {
            if (settings == null) return;
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.Debug("Saved floor boundary sampling settings to {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save floor boundary sampling settings");
            }
        }
    }
}
