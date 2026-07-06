using System;
using System.IO;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Loads and saves last-used Toposolid tool under %LocalAppData%\EFFE\effetopo.
    /// </summary>
    public class ToposolidToolsSettingsService
    {
        private const string SettingsFileName = "toposolid_tools.json";

        private static ToposolidToolsSettingsService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static ToposolidToolsSettingsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ToposolidToolsSettingsService();
                    }
                }
                return _instance;
            }
        }

        private ToposolidToolsSettingsService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public ToposolidToolsSettings Load()
        {
            try
            {
                string path = _storage.GetFilePath(SettingsFileName);
                if (!File.Exists(path))
                    return new ToposolidToolsSettings();

                string json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<ToposolidToolsSettings>(json);
                return settings ?? new ToposolidToolsSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load Toposolid tools settings");
                return new ToposolidToolsSettings();
            }
        }

        public void SaveLastUsedCommand(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId)) return;
            try
            {
                var settings = new ToposolidToolsSettings { LastUsedCommandId = commandId };
                string path = _storage.GetFilePath(SettingsFileName);
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.Debug("Saved last used Toposolid tool: {CommandId}", commandId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save Toposolid tools settings");
            }
        }
    }
}
