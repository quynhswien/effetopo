using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using effetopo.Services;
using JetBrains.Annotations;

namespace effetopo.Commands
{
    /// <summary>
    /// Command to merge a Proposal Toposolid into an Existing Toposolid (Proposal priority)
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class MergeProposalToposolidCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "MergeProposalToposolid";

        public override string CommandName => COMMAND_NAME;

        protected override Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc.Document;

                if (doc == null)
                {
                    message = "No active document";
                    return Result.Failed;
                }

                // Check if Toposolid is available (Revit 2024+)
#if !REVIT2024_OR_GREATER
                message = "Toposolid elements are only available in Revit 2024 and later";
                RevitNotificationHandler.ShowGeneralMessageDialog("Version Error", message);
                return Result.Failed;
#endif

                Log.Information("Starting MergeProposalToposolid command");

                // Prompt user to select Proposal Toposolid
                ISelectionFilter filter = new ToposolidSelectionFilter();
                Reference proposalRef;

                try
                {
                    proposalRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        filter,
                        "Select the Proposal Toposolid (designed/modified terrain)");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled selection");
                    return Result.Cancelled;
                }

                if (proposalRef == null)
                {
                    message = "Please select a Proposal Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Element proposalElement = doc.GetElement(proposalRef);
#if REVIT2024_OR_GREATER
                if (!(proposalElement is Toposolid proposalToposolid))
                {
                    message = "Selected element is not a Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }
#else
                Element proposalToposolid = proposalElement;
#endif

                Log.Information("Selected Proposal Toposolid: {Id}", GetElementIdValue(proposalToposolid.Id));

                // Prompt user to select Existing Toposolid
                Reference existingRef;
                try
                {
                    existingRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        filter,
                        "Select the Existing Toposolid (original terrain)");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled selection");
                    return Result.Cancelled;
                }

                if (existingRef == null)
                {
                    message = "Please select an Existing Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Element existingElement = doc.GetElement(existingRef);
#if REVIT2024_OR_GREATER
                if (!(existingElement is Toposolid existingToposolid))
                {
                    message = "Selected element is not a Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }
#else
                Element existingToposolid = existingElement;
#endif

                Log.Information("Selected Existing Toposolid: {Id}", GetElementIdValue(existingToposolid.Id));

                // Confirm merge
                var result = System.Windows.MessageBox.Show(
                    $"Merge Proposal Toposolid (ID: {GetElementIdValue(proposalToposolid.Id)}) into " +
                    $"Existing Toposolid (ID: {GetElementIdValue(existingToposolid.Id)})?\n\n" +
                    "• Existing points within proposal boundary will be projected onto proposal surface to get new elevation.\n" +
                    "• Proposal points that don't exist in existing (different XY or different Z) can be ADDED to existing (optional).\n" +
                    "• Points outside existing boundary will NOT be added.\n" +
                    "• Outside proposal boundary, Existing elevations will be preserved.",
                    "Merge Proposal into Existing",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.No)
                {
                    return Result.Cancelled;
                }

                // Proposal points are always used by default.
                const bool alwaysAddProposalPoints = true;
                Log.Information("Proposal points are always added by default: {AlwaysAdd}", alwaysAddProposalPoints);

                // Optional: also use Existing points as reference points on Proposal surface.
                var existingReferenceDialog = new TaskDialog("Existing Reference Points Option");
                existingReferenceDialog.MainInstruction = "Also use Existing reference points on Proposal surface?";
                existingReferenceDialog.MainContent =
                    "Proposal points are always added by default.\n\n" +
                    "• Yes: also project Existing points inside Proposal boundary onto Proposal surface and use them as additional reference points.\n" +
                    "• No (default): delete Existing points inside Proposal boundary, then use Proposal points only.";
                existingReferenceDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Yes - Also use Existing reference points on Proposal surface",
                    "Use both Proposal points and Existing reference points");
                existingReferenceDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    "No - Use only Proposal points",
                    "Delete Existing points inside Proposal boundary first");
                existingReferenceDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
                existingReferenceDialog.DefaultButton = TaskDialogResult.CommandLink2;

                TaskDialogResult existingReferenceResult = existingReferenceDialog.Show();
                if (existingReferenceResult == TaskDialogResult.Cancel)
                {
                    return Result.Cancelled;
                }
                bool useExistingReferencePointsOnProposalSurface = (existingReferenceResult == TaskDialogResult.CommandLink1);
                Log.Information("User chose to use Existing reference points on Proposal surface: {UseExistingReference}",
                    useExistingReferencePointsOnProposalSurface);

                // Boundary cleanup offset: remove/ignore existing points in an outside band near proposal boundary.
                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var cleanupOffsetDialog = new TaskDialog("Boundary Cleanup Offset");
                cleanupOffsetDialog.MainInstruction = "Select Boundary Cleanup Offset";
                cleanupOffsetDialog.MainContent =
                    "Existing points in the outside offset band near Proposal boundary will be removed/ignored to avoid Toposolid errors.\n\n" +
                    "Outside this band, Existing points are preserved.";
                if (useMillimeters)
                {
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "300 mm", "Default");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "1500 mm", "");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "3000 mm", "");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "4500 mm", "");
                }
                else
                {
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "1'", "Default");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "5'", "");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "10'", "");
                    cleanupOffsetDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "15'", "");
                }
                cleanupOffsetDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
                cleanupOffsetDialog.DefaultButton = TaskDialogResult.CommandLink1;

                TaskDialogResult cleanupOffsetResult = cleanupOffsetDialog.Show();
                if (cleanupOffsetResult == TaskDialogResult.Cancel)
                {
                    return Result.Cancelled;
                }

                double boundaryCleanupOffsetFeet = 1.0;
                if (useMillimeters)
                {
#if REVIT2024_OR_GREATER
                    if (cleanupOffsetResult == TaskDialogResult.CommandLink2) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(1500.0, UnitTypeId.Millimeters);
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink3) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(3000.0, UnitTypeId.Millimeters);
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink4) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(4500.0, UnitTypeId.Millimeters);
                    else boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);
#else
                    if (cleanupOffsetResult == TaskDialogResult.CommandLink2) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(1500.0, DisplayUnitType.DUT_MILLIMETERS);
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink3) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(3000.0, DisplayUnitType.DUT_MILLIMETERS);
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink4) boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(4500.0, DisplayUnitType.DUT_MILLIMETERS);
                    else boundaryCleanupOffsetFeet = UnitUtils.ConvertToInternalUnits(300.0, DisplayUnitType.DUT_MILLIMETERS);
#endif
                }
                else
                {
                    if (cleanupOffsetResult == TaskDialogResult.CommandLink2) boundaryCleanupOffsetFeet = 5.0;
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink3) boundaryCleanupOffsetFeet = 10.0;
                    else if (cleanupOffsetResult == TaskDialogResult.CommandLink4) boundaryCleanupOffsetFeet = 15.0;
                    else boundaryCleanupOffsetFeet = 1.0;
                }
                var customCleanupResult = System.Windows.MessageBox.Show(
                    useMillimeters
                        ? "Do you want to enter a custom cleanup offset value in millimeters?"
                        : "Do you want to enter a custom cleanup offset value in feet?",
                    "Custom Cleanup Offset",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question);
                if (customCleanupResult == System.Windows.MessageBoxResult.Cancel)
                {
                    return Result.Cancelled;
                }
                if (customCleanupResult == System.Windows.MessageBoxResult.Yes)
                {
                    double? customOffsetFeet = PromptForCustomCleanupOffsetFeet(useMillimeters);
                    if (!customOffsetFeet.HasValue)
                    {
                        return Result.Cancelled;
                    }
                    boundaryCleanupOffsetFeet = customOffsetFeet.Value;
                }
                Log.Information("User selected boundary cleanup offset: {OffsetFeet} feet", boundaryCleanupOffsetFeet);

                // Ask if user wants to delete proposal after merge
                var deleteResult = System.Windows.MessageBox.Show(
                    "Delete Proposal Toposolid after merge?",
                    "Delete Proposal",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                bool deleteProposal = deleteResult == System.Windows.MessageBoxResult.Yes;

                // Perform merge
                Log.Information("Checking document transaction state before merge");
                bool docIsModifiable = doc.IsModifiable;
                Log.Information("Document.IsModifiable = {IsModifiable}", docIsModifiable);
                
                using (Transaction tx = new Transaction(doc, "Merge Proposal Toposolid"))
                {
                    Log.Information("Starting transaction: {TransactionName}", tx.GetName());
                    tx.Start();
                    Log.Information("Transaction started. Document.IsModifiable = {IsModifiable}", doc.IsModifiable);

                    try
                    {
                        ToposolidMergeService mergeService = ToposolidMergeService.Instance;
                        Log.Information(
                            "Calling MergeProposalIntoExisting service method (deleteProposal={DeleteProposal}, useExistingReference={UseExistingReference}, boundaryCleanupOffsetFeet={BoundaryCleanupOffsetFeet})",
                            deleteProposal, useExistingReferencePointsOnProposalSurface, boundaryCleanupOffsetFeet);
#if REVIT2024_OR_GREATER
                        Toposolid mergedToposolid = mergeService.MergeProposalIntoExisting(
                            doc, proposalToposolid, existingToposolid, deleteProposal, alwaysAddProposalPoints,
                            useExistingReferencePointsOnProposalSurface, boundaryCleanupOffsetFeet, null);
#else
                        Element mergedToposolid = mergeService.MergeProposalIntoExisting(
                            doc, proposalToposolid, existingToposolid, deleteProposal, alwaysAddProposalPoints,
                            useExistingReferencePointsOnProposalSurface, boundaryCleanupOffsetFeet, null);
#endif

                        if (mergedToposolid == null)
                        {
                            message = "Failed to merge Toposolids";
                            Log.Warning("Merge returned null Toposolid, rolling back transaction");
                            tx.RollBack();
                            return Result.Failed;
                        }

                        Log.Information("Merge successful. Committing transaction");
                        tx.Commit();
                        Log.Information("Transaction committed successfully");

                        // Select the new merged Toposolid
                        uidoc.Selection.SetElementIds(new List<ElementId> { mergedToposolid.Id });

                        // Build detailed success message with statistics
                        string successMessage = $"Successfully merged Proposal Toposolid into Existing Toposolid.\n" +
                            $"Toposolid ID: {GetElementIdValue(mergedToposolid.Id)}\n\n" +
                            $"Statistics:\n" +
                            $"• New points added: {mergeService.LastMergePointsAdded}\n" +
                            $"• Existing points updated: {mergeService.LastMergePointsUpdated}\n" +
                            $"• Points skipped: {mergeService.LastMergePointsSkipped}";
                        
                        RevitNotificationHandler.ShowGeneralMessageDialog("Merge Complete", successMessage);

                        Log.Information("Successfully completed MergeProposalToposolid command");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during merge operation");
                        message = $"Error merging Toposolids: {ex.Message}";
                        tx.RollBack();
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in MergeProposalToposolidCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Selection filter for Toposolid elements only
        /// </summary>
        private class ToposolidSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
#if REVIT2024_OR_GREATER
                return elem is Toposolid;
#else
                // In older versions, check by category name
                return elem?.Category?.Name == "Topography" || elem?.Category?.Name == "Toposolid";
#endif
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the ElementId value as string (handles version differences)
        /// </summary>
        private string GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value.ToString() ?? "null";
#else
            return id?.IntegerValue.ToString() ?? "null";
#endif
        }

        private static double? PromptForCustomCleanupOffsetFeet(bool useMillimeters)
        {
            string unitLabel = useMillimeters ? "mm" : "feet";
            string defaultValue = useMillimeters ? "300" : "1";
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                $"Enter Boundary Cleanup Offset ({unitLabel}):",
                "Custom Boundary Cleanup Offset",
                defaultValue);

            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            string normalizedInput = input.Trim().Replace(",", ".");
            if (!double.TryParse(normalizedInput, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0)
            {
                RevitNotificationHandler.ShowGeneralMessageDialog("Invalid value",
                    $"Please enter a positive numeric value in {unitLabel}.");
                return null;
            }

            if (useMillimeters)
            {
#if REVIT2024_OR_GREATER
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
#else
                return UnitUtils.ConvertToInternalUnits(value, DisplayUnitType.DUT_MILLIMETERS);
#endif
            }

            return value;
        }

        private static bool IsProjectUsingMillimeters(Document doc)
        {
            if (doc == null) return false;
            try
            {
                Units units = doc.GetUnits();
#if REVIT2024_OR_GREATER
                FormatOptions lengthFormat = units?.GetFormatOptions(SpecTypeId.Length);
                return lengthFormat != null && lengthFormat.GetUnitTypeId() == UnitTypeId.Millimeters;
#else
                FormatOptions lengthFormat = units?.GetFormatOptions(UnitType.UT_Length);
                return lengthFormat != null && lengthFormat.DisplayUnits == DisplayUnitType.DUT_MILLIMETERS;
#endif
            }
            catch
            {
                return false;
            }
        }
    }
}
