using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.Revit.UI;
using effetopo.Models;
using effetopo.Views;

namespace effetopo.Services
{
    /// <summary>
    /// External event handler for showing notifications from background threads
    /// </summary>
    public class RevitNotificationHandler : IExternalEventHandler
    {
        private static RevitNotificationHandler _instance;
        private static readonly object _lock = new object();
        private Queue<NotificationBase> _notificationQueue = new Queue<NotificationBase>();
        private bool _isProcessing = false;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static RevitNotificationHandler Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new RevitNotificationHandler();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// The external event
        /// </summary>
        public ExternalEvent ExternalEvent { get; private set; }

        private RevitNotificationHandler()
        {
            // Register the external event when the handler is created
            ExternalEvent = ExternalEvent.Create(this);
            Log.Debug("RevitNotificationHandler created and external event registered");
        }

        /// <summary>
        /// Queues a notification and triggers the external event
        /// </summary>
        /// <param name="notification">The notification to show</param>
        public void ShowNotification(NotificationBase notification)
        {
            if (notification == null)
            {
                Log.Warning("Attempted to show null notification");
                return;
            }

            lock (_notificationQueue)
            {
                _notificationQueue.Enqueue(notification);
                Log.Information("Notification of type {Type} queued", notification.Type);
            }

            ExternalEvent.Raise();
            Log.Debug("External event raised for notification");
        }

        /// <summary>
        /// Shortcut method for showing version update notifications
        /// </summary>
        /// <param name="updateInfo">The update information</param>
        public void ShowVersionUpdateNotification(VersionCheckResponse updateInfo)
        {
            if (updateInfo == null)
            {
                Log.Warning("Attempted to show update notification with null data");
                return;
            }

            ShowNotification(new VersionUpdateNotification(updateInfo));
        }

        /// <summary>
        /// Shortcut method for showing license warning notifications
        /// </summary>
        /// <param name="message">The license warning message</param>
        public void ShowLicenseWarningNotification(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                Log.Warning("Attempted to show license warning with empty message");
                return;
            }

            ShowNotification(new LicenseWarningNotification(message));
        }

        /// <summary>
        /// Shortcut method for showing general notifications
        /// </summary>
        /// <param name="title">The notification title</param>
        /// <param name="content">The notification content</param>
        public void ShowGeneralNotification(string title, string content)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
            {
                Log.Warning("Attempted to show general notification with empty title or content");
                return;
            }

            ShowNotification(new GeneralNotification(title, content));
        }

        public void Execute(UIApplication app)
        {
            if (_isProcessing)
            {
                Log.Debug("Already processing a notification, will handle next notification after current one completes");
                return;
            }

            try
            {
                _isProcessing = true;
                NotificationBase notification = null;

                lock (_notificationQueue)
                {
                    if (_notificationQueue.Count > 0)
                    {
                        notification = _notificationQueue.Dequeue();
                    }
                }

                if (notification != null)
                {
                    Log.Information("Processing notification of type {Type}", notification.Type);

                    switch (notification.Type)
                    {
                        case NotificationType.VersionUpdate:
                            var versionNotification = notification as VersionUpdateNotification;
                            ShowUpdateNotificationDialog(versionNotification.UpdateInfo);
                            break;

                        case NotificationType.LicenseWarning:
                            var licenseNotification = notification as LicenseWarningNotification;
                            ShowLicenseWarningDialog(licenseNotification.Message);
                            break;

                        case NotificationType.GeneralMessage:
                            var generalNotification = notification as GeneralNotification;
                            ShowGeneralMessageDialog(generalNotification.Title, generalNotification.Content);
                            break;

                        default:
                            Log.Warning("Unknown notification type: {Type}", notification.Type);
                            break;
                    }

                    // Check if there are more notifications to process
                    if (_notificationQueue.Count > 0)
                    {
                        ExternalEvent.Raise();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in notification external event handler");
                StatisticsCollectorService.Instance.RecordError("NotificationError", ex.Message, ex.StackTrace);
            }
            finally
            {
                _isProcessing = false;
            }
        }

        /// <summary>
        /// Show update notification dialog
        /// </summary>
        /// <param name="updateInfo">The update information to display</param>
        public void ShowUpdateNotificationDialog(VersionCheckResponse updateInfo)
        {
            try
            {
                if (updateInfo != null)
                {
                    // Show the update notification dialog
                    var dialog = new UpdateNotificationView(updateInfo);
                    dialog.Show();

                    // Record this in statistics
                    StatisticsCollectorService.Instance.RecordFeatureUsage(
                        "UpdateNotification",
                        1,
                        new Dictionary<string, object>
                        {
                            { "version", updateInfo.VersionNumber },
                            { "is_mandatory", updateInfo.IsMandatory }
                        });

                    Log.Information("Update notification dialog shown for version {Version}", updateInfo.VersionNumber);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing update notification dialog");
                StatisticsCollectorService.Instance.RecordError("UpdateNotificationDialogError", ex.Message, ex.StackTrace);
            }
        }

        /// <summary>
        /// Shows a license warning dialog
        /// </summary>
        private void ShowLicenseWarningDialog(string message)
        {
            try
            {
                MessageBox.Show(
                    $"This copy of {Application.APP_NAME} is not licensed.\n\n{message}\n\n" +
                    "Please contact your administrator to obtain a valid license.",
                    "License Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Log.Information("License warning dialog shown");

                // Record this in statistics
                StatisticsCollectorService.Instance.RecordFeatureUsage(
                    "LicenseWarningShown",
                    1,
                    new Dictionary<string, object>
                    {
                        { "message", message }
                    });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing license warning dialog");
                StatisticsCollectorService.Instance.RecordError("LicenseWarningDialogError", ex.Message, ex.StackTrace);
            }
        }

        /// <summary>
        /// Shows a general message dialog
        /// </summary>
        public static void ShowGeneralMessageDialog(string title, string content)
        {
            try
            {
                MessageBox.Show(
                    content,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Log.Information("General message dialog shown: {Title}", title);

                // Record this in statistics
                StatisticsCollectorService.Instance.RecordFeatureUsage(
                    "GeneralNotification",
                    1,
                    new Dictionary<string, object>
                    {
                        { "title", title }
                    });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing general message dialog");
                StatisticsCollectorService.Instance.RecordError("GeneralMessageDialogError", ex.Message, ex.StackTrace);
            }
        }

        public string GetName()
        {
            return "RevitNotificationHandler";
        }
    }
}