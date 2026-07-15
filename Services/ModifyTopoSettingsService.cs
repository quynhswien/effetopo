using System;
using System.IO;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    public class ModifyTopoSettingsService
    {
        private const string SettingsFileName = "modify_topo.json";

        private static ModifyTopoSettingsService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static ModifyTopoSettingsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ModifyTopoSettingsService();
                    }
                }
                return _instance;
            }
        }

        private ModifyTopoSettingsService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public ModifyTopoSettings Load()
        {
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                if (!File.Exists(path))
                    return new ModifyTopoSettings();

                string json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<ModifyTopoSettings>(json);
                return settings ?? new ModifyTopoSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load modify topo settings");
                return new ModifyTopoSettings();
            }
        }

        public void Save(ModifyTopoSettings settings)
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
                Log.Warning(ex, "Failed to save modify topo settings");
            }
        }
    }
}
