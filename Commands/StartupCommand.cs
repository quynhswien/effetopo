using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Services;
using effetopo.ViewModels;
using effetopo.Views;
using JetBrains.Annotations;

namespace effetopo.Commands
{
    /// <summary>
    ///     External command entry point invoked from the Revit interface
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class StartupCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "effetopo";
        /// <summary>
        /// Command name
        /// </summary>
        public override string CommandName => COMMAND_NAME;

        protected override Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Record feature usage
                StatisticsCollectorService.Instance.RecordFeatureUsage(COMMAND_NAME + "Command");

                var viewModel = new effetopoViewModel();
                var view = new effetopoView(viewModel);
                view.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in StartupCommand");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}