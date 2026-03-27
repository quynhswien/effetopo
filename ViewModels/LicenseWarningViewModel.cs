using System;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using effetopo.Services;
using effetopo.Views;

namespace effetopo.ViewModels
{
    public class LicenseWarningViewModel : ObservableObject
    {
        private readonly Window _window;
        private readonly string _licenseRequirement;

        private string _warningMessage;
        public string WarningMessage
        {
            get => _warningMessage;
            set => SetProperty(ref _warningMessage, value);
        }

        public bool AllowContinue => _licenseRequirement?.ToLower() == "warn";

        public string ContinueButtonText => "Continue Anyway";

        public ICommand ActivateLicenseCommand { get; }
        public ICommand ContinueCommand { get; }

        public LicenseWarningViewModel(Window window, string warningMessage, string licenseRequirement)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _warningMessage = warningMessage ?? "This feature requires a valid license.";
            _licenseRequirement = licenseRequirement;

            ActivateLicenseCommand = new RelayCommand(ExecuteActivateLicense);
            ContinueCommand = new RelayCommand(ExecuteContinue);

            Log.Debug("License warning shown with requirement: {Requirement}", licenseRequirement);
        }

        private void ExecuteActivateLicense()
        {
            try
            {
                var activationView = new LicenseActivationView();
                activationView.Show();

                _window.DialogResult = true;
                _window.Close();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing license activation dialog");
            }
        }

        private void ExecuteContinue()
        {
            if (AllowContinue)
            {
                _window.DialogResult = false;
                _window.Close();
            }
        }
    }
}