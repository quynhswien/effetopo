using System;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Services;
using effetopo.Views;

namespace effetopo.Commands
{
    /// <summary>
    /// Base command class that checks for a valid license before execution
    /// </summary>
    public abstract class BaseCommand : IExternalCommand
    {
        public abstract string CommandName { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Initialize logger
                LoggingService.InitializeLogger(forceReinitialization: true);

                Log.Information("Executing command {Command}", CommandName);

                // Check for updates on first command execution
                VersionCheckService.CheckVersionIfNeeded();

                // Get the license check service
                LicenseCheckService licenseService = LicenseCheckService.Instance;

                // Get the license requirement level (enforce, warn, none)
                // First check server response, if not available use the application constant
                string licenseRequirement = licenseService.CachedResponse?.LicenseRequirement?.ToLower()
                    ?? Application.LICENSE_REQUIREMENT.ToLower();

                Log.Information("License requirement: {LicenseRequirement}", licenseRequirement);

                // If requirement is none, execute without checking
                if (licenseRequirement == "none")
                {
                    TrackCommandUsage();
                    return ExecuteCommand(commandData, ref message, elements);
                }

                Log.Information("Is licensed: {IsLicensed}", Application.IsLicensed);

                // Check if the license is valid
                if (!Application.IsLicensed)
                {
                    // Trigger a background license check every time the addin is used
                    Log.Information("Triggering background license check");
                    // Run license check asynchronously to avoid blocking UI
                    BackgroundTaskService.Instance.RunBackgroundTask(async token =>
                        await licenseService.CheckAndUpdateLicenseAsync(true));

                    // If still not licensed, show warning
                    if (!ShowLicenseWarning(licenseRequirement))
                    {
                        Log.Warning("License check failed, user opted not to continue");
                        return Result.Cancelled;
                    }
                }

                // At this point the license check passed or was skipped, proceed with the command
                Log.Information("License check passed for command {Command}", CommandName);

                // Record command usage
                TrackCommandUsage();

                return ExecuteCommand(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing command {Command}", CommandName);
                message = $"An error occurred: {ex.Message}";
                StatisticsCollectorService.Instance.RecordError("CommandError", ex.Message, ex.StackTrace);
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Cancelled;
            }
        }

        /// <summary>
        /// Abstract method to be implemented by derived commands
        /// </summary>
        protected abstract Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements);

        private void TrackCommandUsage()
        {
            StatisticsCollectorService.Instance.RecordFeatureUsage(CommandName);

            if (ToposolidToolsRibbonService.Instance.IsToposolidToolCommand(CommandName))
                ToposolidToolsRibbonService.Instance.RecordCommandUsed(CommandName);
        }

        /// <summary>
        /// Shows a warning message about the license status and handles license requirements
        /// </summary>
        /// <param name="licenseRequirement">The license requirement level (enforce or warn)</param>
        /// <returns>True if execution can continue, false if it should be blocked</returns>
        private bool ShowLicenseWarning(string licenseRequirement)
        {
            try
            {
                string warningMessage = Application.LicenseStatusMessage;
                if (string.IsNullOrEmpty(warningMessage))
                {
                    warningMessage = "This command requires a valid license. Please activate a license to continue.";
                }

                // Use our custom warning dialog which allows activation
                var licenseWarningView = new LicenseWarningView(warningMessage, licenseRequirement);
                bool? result = licenseWarningView.ShowDialog();

                // Allow continue only if requirement is warn and user clicked Continue
                return licenseRequirement == "warn" && result == false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error showing license warning");
                return false;
            }
        }
    }
}