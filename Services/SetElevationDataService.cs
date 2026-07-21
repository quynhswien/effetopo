using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using effetopo.Models;
using Newtonsoft.Json;

namespace effetopo.Services
{
    /// <summary>
    /// Persists linked Set Elevation data to project metadata (Extensible Storage) and local JSON files.
    /// </summary>
    public class SetElevationDataService
    {
        private static readonly Guid SchemaGuid = new Guid("7c4e9a12-3b5d-4f8e-9c21-5a6d8e0f1b2c");
        private const string FieldName = "SetElevationDataJson";

        private static SetElevationDataService _instance;
        private static readonly object _lock = new object();

        private readonly LocalStorageService _storage;

        public static SetElevationDataService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SetElevationDataService();
                    }
                }
                return _instance;
            }
        }

        private SetElevationDataService()
        {
            _storage = new LocalStorageService(Application.APP_NAME);
        }

        public SetElevationProjectData Load(Document doc)
        {
            if (doc == null) return CreateEmptyData(doc);

            SetElevationProjectData localData = Normalize(LoadLocal(doc));
            SetElevationProjectData projectData = Normalize(LoadFromProjectMetadata(doc));

            if (projectData != null && projectData.Lines.Count > 0)
            {
                if (localData == null || localData.LastUpdated < projectData.LastUpdated)
                    return projectData;
            }

            return localData ?? projectData ?? CreateEmptyData(doc);
        }

        /// <summary>
        /// Schema must be registered outside of any open transaction.
        /// </summary>
        public void EnsureSchemaRegistered()
        {
            GetOrCreateSchema();
        }

        public void Save(Document doc, SetElevationProjectData data)
        {
            Save(doc, data, includeProjectMetadata: true, includeLocalFile: true);
        }

        public void Save(Document doc, SetElevationProjectData data, bool includeProjectMetadata, bool includeLocalFile = true)
        {
            if (doc == null || data == null) return;

            data.ProjectUniqueId = doc.ProjectInformation?.UniqueId ?? string.Empty;
            data.ProjectName = doc.Title ?? string.Empty;
            data.LastUpdated = DateTime.UtcNow;

            if (includeLocalFile)
                SaveLocal(doc, data);

            if (includeProjectMetadata)
                SaveToProjectMetadata(doc, data);
        }

        private static SetElevationProjectData? Normalize(SetElevationProjectData? data)
        {
            if (data == null) return null;
            data.Lines ??= new System.Collections.Generic.List<SetElevationLineRecord>();
            return data;
        }

        public SetElevationLineRecord? FindRecord(SetElevationProjectData data, long curveElementId)
        {
            if (data?.Lines == null) return null;
            foreach (SetElevationLineRecord record in data.Lines)
            {
                if (record.CurveElementId == curveElementId)
                    return record;
            }
            return null;
        }

        private SetElevationProjectData? LoadLocal(Document doc)
        {
            try
            {
                string path = GetLocalFilePath(doc);
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SetElevationProjectData>(json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load local set elevation data");
                return null;
            }
        }

        private void SaveLocal(Document doc, SetElevationProjectData data)
        {
            try
            {
                string path = GetLocalFilePath(doc);
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.Debug("Saved set elevation local data to {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save local set elevation data");
            }
        }

        private SetElevationProjectData? LoadFromProjectMetadata(Document doc)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;

                ProjectInfo projectInfo = doc.ProjectInformation;
                if (projectInfo == null) return null;

                Entity entity = projectInfo.GetEntity(schema);
                if (!entity.IsValid()) return null;

                string json = entity.Get<string>(FieldName);
                if (string.IsNullOrWhiteSpace(json)) return null;

                return JsonConvert.DeserializeObject<SetElevationProjectData>(json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load set elevation project metadata");
                return null;
            }
        }

        private void SaveToProjectMetadata(Document doc, SetElevationProjectData data)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null)
                {
                    Log.Warning("Set elevation schema is not registered. Call EnsureSchemaRegistered outside a transaction.");
                    return;
                }

                ProjectInfo projectInfo = doc.ProjectInformation;
                if (projectInfo == null) return;

                string json = JsonConvert.SerializeObject(data);
                var entity = new Entity(schema);
                entity.Set(FieldName, json);
                projectInfo.SetEntity(entity);
                Log.Debug("Saved set elevation data to project metadata");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save set elevation project metadata");
            }
        }

        private static Schema GetOrCreateSchema()
        {
            Schema existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("EffeSetElevation");
            builder.SetDocumentation("Linked model line elevation assignments from effetopo Set Elevation.");
            builder.AddSimpleField(FieldName, typeof(string));
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            return builder.Finish();
        }

        private static SetElevationProjectData CreateEmptyData(Document doc)
        {
            return new SetElevationProjectData
            {
                ProjectUniqueId = doc?.ProjectInformation?.UniqueId ?? string.Empty,
                ProjectName = doc?.Title ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Lines = new System.Collections.Generic.List<SetElevationLineRecord>()
            };
        }

        private string GetLocalFilePath(Document doc)
        {
            string projectId = doc?.ProjectInformation?.UniqueId ?? "unknown";
            return _storage.GetFilePath($"set_elevation_data_{projectId}.json");
        }
    }
}
