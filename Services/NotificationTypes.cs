using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Base class for all notification types
    /// </summary>
    public abstract class NotificationBase
    {
        /// <summary>
        /// Type of notification
        /// </summary>
        public NotificationType Type { get; protected set; }
    }

    /// <summary>
    /// Types of notifications that can be shown
    /// </summary>
    public enum NotificationType
    {
        VersionUpdate,
        LicenseWarning,
        GeneralMessage
    }

    /// <summary>
    /// Notification for version updates
    /// </summary>
    public class VersionUpdateNotification : NotificationBase
    {
        /// <summary>
        /// Version update information
        /// </summary>
        public VersionCheckResponse UpdateInfo { get; private set; }

        public VersionUpdateNotification(VersionCheckResponse updateInfo)
        {
            Type = NotificationType.VersionUpdate;
            UpdateInfo = updateInfo;
        }
    }

    /// <summary>
    /// Notification for license warnings
    /// </summary>
    public class LicenseWarningNotification : NotificationBase
    {
        /// <summary>
        /// License status message
        /// </summary>
        public string Message { get; private set; }

        public LicenseWarningNotification(string message)
        {
            Type = NotificationType.LicenseWarning;
            Message = message;
        }
    }

    /// <summary>
    /// General purpose notification
    /// </summary>
    public class GeneralNotification : NotificationBase
    {
        /// <summary>
        /// Title of the notification
        /// </summary>
        public string Title { get; private set; }

        /// <summary>
        /// Content of the notification
        /// </summary>
        public string Content { get; private set; }

        public GeneralNotification(string title, string content)
        {
            Type = NotificationType.GeneralMessage;
            Title = title;
            Content = content;
        }
    }
}