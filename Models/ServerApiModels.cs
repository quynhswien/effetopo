using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace effetopo.Models
{
    #region Addin Instance Models

    /// <summary>
    /// Information about the addin instance
    /// </summary>
    public class AddinInstance
    {
        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        [JsonProperty("device_name")]
        public string DeviceName { get; set; }

        [JsonProperty("operating_system")]
        public string OperatingSystem { get; set; }

        [JsonProperty("os_version")]
        public string OsVersion { get; set; }

        [JsonProperty("app_version")]
        public string AppVersion { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }
    }
    #endregion

    #region Version Check Models

    /// <summary>
    /// Request model for checking version
    /// </summary>
    public class VersionCheckRequest
    {
        [JsonProperty("addin_code")]
        public string AddinCode { get; set; }

        [JsonProperty("current_version")]
        public string CurrentVersion { get; set; }

        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        [JsonProperty("revit_version")]
        public string RevitVersion { get; set; }

        [JsonProperty("include_prerelease")]
        public bool IncludePrerelease { get; set; }
    }

    /// <summary>
    /// Response model for version check
    /// </summary>
    public class VersionCheckResponse
    {
        [JsonProperty("version_number")]
        public string VersionNumber { get; set; }

        [JsonProperty("version_id")]
        public string VersionId { get; set; }

        [JsonProperty("release_date")]
        public DateTime ReleaseDate { get; set; }

        [JsonProperty("is_prerelease")]
        public bool IsPrerelease { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        [JsonProperty("release_notes")]
        public string ReleaseNotes { get; set; }

        [JsonProperty("is_mandatory")]
        public bool IsMandatory { get; set; }
    }

    #endregion

    #region License Check Models

    /// <summary>
    /// Request model for checking license
    /// </summary>
    public class LicenseCheckRequest
    {
        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        [JsonProperty("product_code")]
        public string ProductCode { get; set; }

        [JsonProperty("license_key")]
        public string LicenseKey { get; set; }

        [JsonProperty("revit_version")]
        public string RevitVersion { get; set; }
    }

    /// <summary>
    /// Response model for license check
    /// </summary>
    public class LicenseCheckResponse
    {
        [JsonProperty("is_licensed")]
        public bool IsLicensed { get; set; }

        [JsonProperty("license_type")]
        public string LicenseType { get; set; } // lifetime/subscription/organization

        [JsonProperty("license_requirement")]
        public string LicenseRequirement { get; set; } // enforce/warn/none

        [JsonProperty("expiry_date")]
        public DateTime? ExpiryDate { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; } // active/inactive/expired/invalid/seats_exceeded/not_required/none

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    /// <summary>
    /// Request to activate a license
    /// </summary>
    public class LicenseActivationRequest
    {
        [JsonProperty("license_key")]
        public string LicenseKey { get; set; }

        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        [JsonProperty("machine_name")]
        public string MachineName { get; set; }

        [JsonProperty("product_code")]
        public string ProductCode { get; set; }
    }

    /// <summary>
    /// Response for license activation
    /// </summary>
    public class LicenseActivationResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("expiry_date")]
        public DateTime? ExpiryDate { get; set; }

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("active_machines")]
        public int? ActiveMachines { get; set; }
    }

    /// <summary>
    /// Request to send a license heartbeat
    /// </summary>
    public class LicenseHeartbeatRequest
    {
        [JsonProperty("machine_id")]
        public string MachineId { get; set; }

        [JsonProperty("license_key")]
        public string LicenseKey { get; set; }
    }

    /// <summary>
    /// Response for license heartbeat
    /// </summary>
    public class LicenseHeartbeatResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }

        [JsonProperty("expiry_date")]
        public DateTime? ExpiryDate { get; set; }
    }

    #endregion

    #region Statistics Models

    /// <summary>
    /// Payload for statistics upload
    /// </summary>
    public class StatsPayload
    {
        [JsonProperty("addin_code")]
        public string AddinCode { get; set; }

        [JsonProperty("addin_instance")]
        public AddinInstance AddinInstance { get; set; }

        [JsonProperty("session_events")]
        public List<SessionEvent> SessionEvents { get; set; }

        [JsonProperty("feature_usage")]
        public List<FeatureUsage> FeatureUsage { get; set; }

        [JsonProperty("error_events")]
        public List<ErrorEvent> ErrorEvents { get; set; }
    }

    /// <summary>
    /// Information about a session event (start/end)
    /// </summary>
    public class SessionEvent
    {
        [JsonProperty("event_type")]
        public string EventType { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("duration")]
        public int? Duration { get; set; }

        [JsonProperty("additional_data")]
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    /// <summary>
    /// Information about feature usage
    /// </summary>
    public class FeatureUsage
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("feature_name")]
        public string FeatureName { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("duration")]
        public int? Duration { get; set; }

        [JsonProperty("additional_data")]
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    /// <summary>
    /// Information about an error event
    /// </summary>
    public class ErrorEvent
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("error_type")]
        public string ErrorType { get; set; }

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; }

        [JsonProperty("stack_trace")]
        public string StackTrace { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("additional_data")]
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    #endregion
}