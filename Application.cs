using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.UI;
using effetopo.Commands;
using effetopo.Models;
using effetopo.Services;
using effetopo.Views;
using Nice3point.Revit.Extensions;

namespace effetopo
{
    /// <summary>
    /// Application entry point for the Revit add-in
    /// </summary>
    public class Application : IExternalApplication
    {
        #region Constants and Static Properties

        /// <summary>
        /// Base name used for storage folder and display
        /// </summary>
        public const string BASE_NAME = "EFFE";

        /// <summary>
        /// Application name used for storage folder and display
        /// </summary>
        public const string APP_NAME = "effetopo";

        /// <summary>
        /// Application unique identifier
        /// </summary>
        public const string APP_ID = "effetopo";

        /// <summary>
        /// Application version
        /// </summary>
        public const string APP_VERSION = "1.0.0";

        /// <summary>
        /// License requirement level (enforce, warn, none)
        /// </summary>
        public const string LICENSE_REQUIREMENT = "none";

        /// <summary>
        /// Revit version number
        /// </summary>
        public static string RevitVersionNumber { get; set; }

        /// <summary>
        /// Keeps track of the application license status
        /// </summary>
        public static bool IsLicensed { get; set; }

        /// <summary>
        /// License status message for display to the user
        /// </summary>
        public static string LicenseStatusMessage { get; set; }

        /// <summary>
        /// Singleton instance of the application
        /// </summary>
        public static Application Instance { get; private set; }

        #endregion

        #region Instance Properties

        /// <summary>
        /// Revit UI controlled application
        /// </summary>
        public UIControlledApplication UIControlledApplication { get; private set; }

        #endregion

        #region IExternalApplication Implementation

        /// <summary>
        /// Called when Revit starts up
        /// </summary>
        /// <param name="uiApp">The Revit UI application</param>
        /// <returns>Result of the startup process</returns>
        public Result OnStartup(UIControlledApplication uiApp)
        {
            try
            {
                Log.Information("Starting application initialization");

                // Initialize application
                Instance = this;
                UIControlledApplication = uiApp;
                RevitVersionNumber = uiApp.ControlledApplication.VersionNumber;

                // Initialize services
                Initialize();

                Log.Information("Addin started successfully");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error during addin startup");

                // Attempt to show error to user
                try
                {
                    MessageBox.Show($"Error starting {APP_NAME}: {ex.Message}",
                        "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    // Suppress any errors in showing the message box
                }

                return Result.Succeeded;
            }
        }

        /// <summary>
        /// Called when Revit shuts down
        /// </summary>
        /// <param name="uiApp">The Revit UI application</param>
        /// <returns>Result of the shutdown process</returns>
        public Result OnShutdown(UIControlledApplication uiApp)
        {
            try
            {
                Log.Information("Starting application shutdown");

                Deinitialize();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during addin shutdown");
                return Result.Succeeded;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the UIApplication from the UIControlledApplication
        /// </summary>
        public static UIApplication GetUIApplication(UIControlledApplication uiApp)
        {
            var type = typeof(UIControlledApplication);

            var propertie = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(e => e.FieldType == typeof(UIApplication));

            return propertie?.GetValue(uiApp) as UIApplication;
        }

        /// <summary>
        /// Initialize all services required by the application
        /// </summary>
        private void Initialize()
        {
            try
            {
                // Create ribbon UI
                CreateRibbonUI();

                // Initialize logger
                LoggingService.InitializeLogger();

                // Force initialization of singleton services
                var serverService = ManageServerService.Instance;
                Log.Debug("ManageServerService initialized");

                // Initialize the notification handler (creates the external event)
                var notificationHandler = RevitNotificationHandler.Instance;
                Log.Debug("Notification handler initialized");

                // Check license in background
                BackgroundTaskService.Instance.RunBackgroundTask(async token =>
                    await LicenseCheckService.Instance.CheckAndUpdateLicenseAsync(token: token));

                // Don't check for updates at startup - will be checked on first command execution
                Log.Information("Version check will be performed when a command is first executed");

                // Start recording session
                StatisticsCollectorService.Instance.RecordSessionStart();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error initializing services");
                throw; // Re-throw to be handled by the calling method
            }
        }

        /// <summary>
        /// Deinitializes the application
        /// </summary>
        private void Deinitialize()
        {

            // Record session end
            StatisticsCollectorService.Instance.RecordSessionEnd();
            StatisticsCollectorService.Instance.Shutdown();

            // Cancel all background tasks
            BackgroundTaskService.Instance.CancelAllTasks();

            // Shutdown logging service
            LoggingService.Shutdown();
        }

        /// <summary>
        /// Creates the ribbon interface for the add-in
        /// </summary>
        private void CreateRibbonUI()
        {
            try
            {
                // Create panel in Revit ribbon
                var panel = UIControlledApplication.CreatePanel("Commands", BASE_NAME);

                // Add commands to panel
                AddCommandsToPanel(panel);

                Log.Debug("Ribbon UI created successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating ribbon UI");
                throw; // Re-throw to be handled by the calling method
            }
        }

        /// <summary>
        /// Adds command buttons to the specified panel
        /// </summary>
        private void AddCommandsToPanel(RibbonPanel panel)
        {
            ToposolidToolsRibbonService.Instance.CreateSplitButton(panel);
        }

        #endregion
    }
}