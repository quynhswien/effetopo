using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using effetopo.Models;
using effetopo.Services;
using effetopo.Views;
using JetBrains.Annotations;

namespace effetopo.Commands
{
    /// <summary>
    /// Command to make a Floor follow a Toposolid surface
    /// Projects floor boundary onto toposolid and updates floor points
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class FloorFollowTopoCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "FloorFollowTopo";

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

                Log.Information("Starting FloorFollowTopo command");

                // Prompt user to select Floor
                ISelectionFilter floorFilter = new FloorSelectionFilter();
                Reference floorRef;

                try
                {
                    floorRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        floorFilter,
                        "Select the Floor to modify");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled selection");
                    return Result.Cancelled;
                }

                if (floorRef == null)
                {
                    message = "Please select a Floor";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Element floorElement = doc.GetElement(floorRef);
                if (!(floorElement is Floor floor))
                {
                    message = "Selected element is not a Floor";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Log.Information("Selected Floor: {Id}", GetElementIdValue(floor.Id));

                // Prompt user to select Toposolid
                ISelectionFilter topoFilter = new ToposolidSelectionFilter();
                Reference topoRef;
                
                try
                {
                    topoRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        topoFilter,
                        "Select the Toposolid surface to follow");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled selection");
                    return Result.Cancelled;
                }

                if (topoRef == null)
                {
                    message = "Please select a Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Element topoElement = doc.GetElement(topoRef);
#if REVIT2024_OR_GREATER
                if (!(topoElement is Toposolid toposolid))
                {
                    message = "Selected element is not a Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }
#else
                Element toposolid = topoElement;
#endif

                Log.Information("Selected Toposolid: {Id}", GetElementIdValue(toposolid.Id));

                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var samplingDialog = new FloorBoundarySamplingDialog(useMillimeters);
                if (samplingDialog.ShowDialog() != true || samplingDialog.SelectedOptions == null)
                {
                    Log.Information("User cancelled boundary sampling dialog");
                    return Result.Cancelled;
                }

                FloorBoundarySamplingOptions boundarySampling = samplingDialog.SelectedOptions;
                string samplingSummary = boundarySampling.Mode == BoundarySampleMode.ByDistance
                    ? $"Boundary points: by distance (~{(useMillimeters ? boundarySampling.SpacingFeet * 304.8 : boundarySampling.SpacingFeet):F2} {(useMillimeters ? "mm" : "ft")} spacing per curve)"
                    : $"Boundary points: {boundarySampling.SegmentsPerCurve} segments per curve ({boundarySampling.SegmentsPerCurve + 1} points including endpoints)";

                var result = System.Windows.MessageBox.Show(
                    $"Make Floor (ID: {GetElementIdValue(floor.Id)}) follow " +
                    $"Toposolid surface (ID: {GetElementIdValue(toposolid.Id)})?\n\n" +
                    samplingSummary + "\n\n" +
                    "Floor points within and on the boundary will be projected onto Toposolid surface.\n" +
                    "All Toposolid points within Floor boundary will be added to Floor.",
                    "Floor Follow Toposolid",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.No)
                {
                    return Result.Cancelled;
                }

                Log.Information("User selected boundary sampling: {Summary}", samplingSummary);

                // Perform operation
                Log.Information("Checking document transaction state before operation");
                bool docIsModifiable = doc.IsModifiable;
                Log.Information("Document.IsModifiable = {IsModifiable}", docIsModifiable);
                
                using (Transaction tx = new Transaction(doc, "Floor Follow Toposolid"))
                {
                    Log.Information("Starting transaction: {TransactionName}", tx.GetName());
                    tx.Start();
                    Log.Information("Transaction started. Document.IsModifiable = {IsModifiable}", doc.IsModifiable);

                    try
                    {
                        ToposolidMergeService mergeService = ToposolidMergeService.Instance;
                        Log.Information("Calling FloorFollowToposolid service method");
#if REVIT2024_OR_GREATER
                        Floor updatedFloor = mergeService.FloorFollowToposolid(doc, floor, toposolid, boundarySampling);
#else
                        Floor updatedFloor = mergeService.FloorFollowToposolid(doc, floor, toposolid, boundarySampling);
#endif

                        if (updatedFloor == null)
                        {
                            message = "Failed to update Floor";
                            Log.Warning("Operation returned null Floor, rolling back transaction");
                            tx.RollBack();
                            return Result.Failed;
                        }

                        Log.Information("Operation successful. Committing transaction");
                        tx.Commit();
                        Log.Information("Transaction committed successfully");

                        // Select the updated Floor
                        uidoc.Selection.SetElementIds(new List<ElementId> { updatedFloor.Id });

                        // Build detailed success message with statistics
                        string successMessage = $"Successfully updated Floor to follow Toposolid surface.\n" +
                            $"Floor ID: {GetElementIdValue(updatedFloor.Id)}\n\n" +
                            $"Statistics:\n" +
                            $"• Points applied (one per XY, elevation from topo): {mergeService.LastFloorFollowBoundaryPointsUpdated}\n" +
                            $"• Points skipped: {mergeService.LastFloorFollowPointsSkipped}";
                        if (mergeService.LastFloorFollowPointsAdjustedByAverage > 0)
                            successMessage += $"\n• Points adjusted by averaging: {mergeService.LastFloorFollowPointsAdjustedByAverage}\n" +
                                "(These points could not be set directly by Revit; elevation was set to the average of the 2 nearest neighbors.)";
                        if (mergeService.LastFloorFollowPointsSkipped > 0)
                            successMessage += "\n\nSome points were not accepted by Revit (you can adjust them manually in Slab Shape Editor).";
                        
                        RevitNotificationHandler.ShowGeneralMessageDialog("Floor Update Complete", successMessage);

                        Log.Information("Successfully completed FloorFollowTopo command");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during Floor follow Topo operation");
                        message = $"Error updating Floor: {ex.Message}";
                        tx.RollBack();
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in FloorFollowTopoCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Selection filter for Floor elements only
        /// </summary>
        private class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Floor;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
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
    }
}
