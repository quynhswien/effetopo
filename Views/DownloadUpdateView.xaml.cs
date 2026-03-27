using System;
using System.Windows;
using effetopo.Models;
using effetopo.Services;
using effetopo.ViewModels;

namespace effetopo.Views
{
    /// <summary>
    /// Interaction logic for UpdateNotificationView.xaml
    /// </summary>
    public partial class UpdateNotificationView : Window
    {
        private UpdateNotificationViewModel _viewModel;

        /// <summary>
        /// Creates a new update notification view
        /// </summary>
        public UpdateNotificationView()
        {
            // Empty constructor for designer
            try
            {
                InitializeComponent();
            }
            catch
            {
                // Ignore errors in design mode
            }
        }

        /// <summary>
        /// Creates a new update notification view with update info
        /// </summary>
        /// <param name="updateInfo">Update information to display</param>
        public UpdateNotificationView(VersionCheckResponse updateInfo)
        {
            try
            {
                // Create and set the view model first
                _viewModel = new UpdateNotificationViewModel(updateInfo);
                DataContext = _viewModel;

                // Handle the close request
                _viewModel.RequestClose += (s, e) => Close();

                try
                {
                    // Initialize UI components - this will be available once XAML is compiled
                    InitializeComponent();
                }
                catch (Exception ex)
                {
                    // If InitializeComponent fails, the XAML file might not be properly set up
                    Log.Error(ex, "Failed to initialize update notification UI");
                    throw;
                }

                Log.Information("Update notification shown for version {Version}", updateInfo.VersionNumber);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating update notification view");
                throw;
            }
        }
    }
}