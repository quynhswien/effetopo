using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using effetopo.Services;

namespace effetopo.ViewModels
{
    public class LicenseActivationViewModel : ObservableObject
    {
        private readonly Window _window;
        private readonly LicenseCheckService _licenseService;
        private string _licenseKey = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _isActivating = false;
        private Brush _statusColor = Brushes.Red;
        private bool _activationSuccessful = false;

        private RelayCommand _activateCommand;

        public string LicenseKey
        {
            get => _licenseKey;
            set
            {
                SetProperty(ref _licenseKey, value);
                _activateCommand?.NotifyCanExecuteChanged();
                if (string.IsNullOrWhiteSpace(LicenseKey))
                {
                    StatusColor = Brushes.Red;
                    StatusMessage = "Please enter a valid license key";
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsActivating
        {
            get => _isActivating;
            set
            {
                SetProperty(ref _isActivating, value);
                _activateCommand?.NotifyCanExecuteChanged();
            }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        public bool ActivationSuccessful => _activationSuccessful;

        public bool CanActivate => !string.IsNullOrWhiteSpace(LicenseKey) && !IsActivating;

        public ICommand ActivateCommand { get; }
        public ICommand CancelCommand { get; }

        public LicenseActivationViewModel(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _licenseService = LicenseCheckService.Instance;

            _activateCommand = new RelayCommand(ExecuteActivate, CanExecuteActivate);
            ActivateCommand = _activateCommand;
            CancelCommand = new RelayCommand(ExecuteCancel);
        }

        private bool CanExecuteActivate() => !string.IsNullOrWhiteSpace(LicenseKey) && !IsActivating;

        private async void ExecuteActivate()
        {
            try
            {
                StatusMessage = string.Empty;
                IsActivating = true;

                StatisticsCollectorService.Instance.RecordFeatureUsage("LicenseActivationAttempt");
                Log.Information("Attempting to activate license with key: {Key}",
                    LicenseKey.Substring(0, Math.Min(4, LicenseKey.Length)) + "****");

                var result = await _licenseService.ActivateLicenseAsync(LicenseKey);

                if (result == null)
                {
                    ShowError("Failed to contact license server. Please check your internet connection and try again.");
                    return;
                }

                if (result.Status == "success")
                {
                    StatusColor = Brushes.Green;
                    StatusMessage = "License activated successfully!";

                    _window.Close();
                }
                else
                {
                    string errorMessage = result.Error ?? "Unknown error";
                    ShowError($"License activation failed: {errorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error activating license");
                ShowError($"An error occurred: {ex.Message}");
                StatisticsCollectorService.Instance.RecordError("LicenseActivationError", ex.Message, ex.StackTrace);
            }
            finally
            {
                IsActivating = false;
            }
        }

        private void ShowError(string message)
        {
            StatusColor = Brushes.Red;
            StatusMessage = message;
        }

        private void ExecuteCancel()
        {
            _window.Close();
        }
    }
}