using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using effetopo.Models;
using effetopo.Services;

namespace effetopo.ViewModels
{
    /// <summary>
    /// ViewModel for the update notification window
    /// </summary>
    public class UpdateNotificationViewModel : ObservableObject
    {
        private VersionCheckResponse _updateInfo;
        private bool _isDownloading;
        private string _statusMessage;
        private double _downloadProgress;
        private bool _autoRunAfterClose;

        /// <summary>
        /// Event raised when the view should be closed
        /// </summary>
        public event EventHandler RequestClose;

        /// <summary>
        /// Update information from the server
        /// </summary>
        public VersionCheckResponse UpdateInfo
        {
            get => _updateInfo;
            set => SetProperty(ref _updateInfo, value);
        }

        /// <summary>
        /// Flag indicating if download is in progress
        /// </summary>
        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetProperty(ref _isDownloading, value);
        }

        /// <summary>
        /// Status message displayed to user
        /// </summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>
        /// Download progress (0-100)
        /// </summary>
        public double DownloadProgress
        {
            get => _downloadProgress;
            set => SetProperty(ref _downloadProgress, value);
        }

        /// <summary>
        /// Whether to auto-run the installer after Revit closes
        /// </summary>
        public bool AutoRunAfterClose
        {
            get => _autoRunAfterClose;
            set => SetProperty(ref _autoRunAfterClose, value);
        }

        /// <summary>
        /// Current application name
        /// </summary>
        public string CurrentAppName => Application.APP_NAME;

        /// <summary>
        /// Command for downloading the update
        /// </summary>
        public ICommand DownloadUpdateCommand { get; }

        /// <summary>
        /// Command for dismissing the notification
        /// </summary>
        public ICommand DismissCommand { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="updateInfo">Update information from version check</param>
        public UpdateNotificationViewModel(VersionCheckResponse updateInfo)
        {
            UpdateInfo = updateInfo;
            StatusMessage = $"Version {updateInfo.VersionNumber} is available to download. Current version: {Application.APP_VERSION}";

            DownloadUpdateCommand = new RelayCommand(DownloadUpdate, () => !IsDownloading);
            DismissCommand = new RelayCommand(Dismiss);

            // Record feature usage
            StatisticsCollectorService.Instance.RecordFeatureUsage("UpdateNotificationShown");
        }

        /// <summary>
        /// Downloads the update
        /// </summary>
        private async void DownloadUpdate()
        {
            try
            {
                IsDownloading = true;
                StatusMessage = "Downloading update...";

                // Download the update file with auto-run option
                string downloadPath = await VersionCheckService.Instance.DownloadVersionAsync(
                    UpdateInfo.VersionId,
                    AutoRunAfterClose);

                if (!string.IsNullOrEmpty(downloadPath))
                {
                    if (AutoRunAfterClose)
                    {
                        StatusMessage = $"Download complete. The installer will run automatically after Revit closes.";
                        // Close the dialog after a successful download with auto-run
                        RequestClose?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        StatusMessage = $"Download complete. File saved to: {downloadPath}";

                        // Open the download folder
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{downloadPath}\"");
                    }
                }
                else
                {
                    StatusMessage = "Download failed. Please try again later.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Error downloading update.";
                Log.Error(ex, "Error downloading update");
                StatisticsCollectorService.Instance.RecordError("UpdateDownloadError", ex.Message, ex.StackTrace);
            }
            finally
            {
                IsDownloading = false;
            }
        }

        /// <summary>
        /// Dismisses the notification dialog
        /// </summary>
        private void Dismiss()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}