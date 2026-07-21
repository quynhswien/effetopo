using System;
using System.IO;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    public class CreateContourLineSettingsService
    {
        private const string SettingsFileName = "create_contour_line.json";

        private static CreateContourLineSettingsService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static CreateContourLineSettingsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new CreateContourLineSettingsService();
                    }
                }
                return _instance;
            }
        }

        private CreateContourLineSettingsService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public CreateContourLineSettings Load()
        {
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                if (!File.Exists(path))
                    return new CreateContourLineSettings();

                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<CreateContourLineSettings>(json) ?? new CreateContourLineSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load create contour line settings");
                return new CreateContourLineSettings();
            }
        }

        public void Save(CreateContourLineSettings settings)
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
                Log.Warning(ex, "Failed to save create contour line settings");
            }
        }
    }
}
