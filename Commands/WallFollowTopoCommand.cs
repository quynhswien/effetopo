using System;
using System.Collections.Generic;
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
    /// Makes a Wall follow a Toposolid surface while preserving wall height.
    /// The wall path is sampled and split into segments with per-segment base offset from topo.
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class WallFollowTopoCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "WallFollowTopo";

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

#if !REVIT2024_OR_GREATER
                message = "Toposolid elements are only available in Revit 2024 and later";
                RevitNotificationHandler.ShowGeneralMessageDialog("Version Error", message);
                return Result.Failed;
#endif

                Log.Information("Starting WallFollowTopo command");

                Reference wallRef;
                try
                {
                    wallRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new WallSelectionFilter(),
                        "Select the Wall to modify");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled wall selection");
                    return Result.Cancelled;
                }

                if (wallRef == null)
                {
                    message = "Please select a Wall";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                if (!(doc.GetElement(wallRef) is Wall wall))
                {
                    message = "Selected element is not a Wall";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Log.Information("Selected Wall: {Id}", GetElementIdValue(wall.Id));

                Reference topoRef;
                try
                {
                    topoRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new ToposolidSelectionFilter(),
                        "Select the Toposolid surface to follow");
                }
                catch (Exception ex) when (ex.Message.Contains("cancelled") || ex.Message.Contains("canceled") || ex.GetType().Name.Contains("Cancel"))
                {
                    Log.Information("User cancelled toposolid selection");
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
#else
                Element toposolid = topoElement;
                if (toposolid == null)
#endif
                {
                    message = "Selected element is not a Toposolid";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                Log.Information("Selected Toposolid: {Id}", GetElementIdValue(toposolid.Id));

                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var savedSettings = FloorBoundarySamplingSettingsService.Instance.Load();
                var samplingDialog = new FloorBoundarySamplingDialog(useMillimeters, savedSettings, isWallMode: true);
                if (samplingDialog.ShowDialog() != true || samplingDialog.SelectedOptions == null)
                {
                    Log.Information("User cancelled path sampling dialog");
                    return Result.Cancelled;
                }

                FloorBoundarySamplingOptions sampling = samplingDialog.SelectedOptions;
                string samplingSummary = sampling.Mode == BoundarySampleMode.ByDistance
                    ? $"by distance (~{(useMillimeters ? sampling.SpacingFeet * 304.8 : sampling.SpacingFeet):F2} {(useMillimeters ? "mm" : "ft")} along path)"
                    : $"{sampling.SegmentsPerCurve} segments along path";
                Log.Information("User selected wall path sampling: {Summary}", samplingSummary);

                using (Transaction tx = new Transaction(doc, "Wall Follow Toposolid"))
                {
                    tx.Start();
                    try
                    {
                        ToposolidMergeService mergeService = ToposolidMergeService.Instance;
#if REVIT2024_OR_GREATER
                        IList<Wall> updatedWalls = mergeService.WallFollowToposolid(doc, wall, toposolid, sampling);
#else
                        IList<Wall> updatedWalls = mergeService.WallFollowToposolid(doc, wall, toposolid, sampling);
#endif

                        if (updatedWalls == null || updatedWalls.Count == 0)
                        {
                            message = "Failed to update Wall";
                            tx.RollBack();
                            return Result.Failed;
                        }

                        tx.Commit();

                        var ids = new List<ElementId>();
                        foreach (Wall w in updatedWalls)
                        {
                            if (w != null)
                                ids.Add(w.Id);
                        }
                        if (ids.Count > 0)
                            uidoc.Selection.SetElementIds(ids);

                        string successMessage =
                            $"Successfully updated Wall to follow Toposolid surface.\n\n" +
                            $"Statistics:\n" +
                            $"• Wall segments created: {mergeService.LastWallFollowSegmentsCreated}\n" +
                            $"• Sample points skipped (no topo hit): {mergeService.LastWallFollowSamplePointsSkipped}";
                        if (mergeService.LastWallFollowSamplePointsSkipped > 0)
                            successMessage += "\n\nSome path samples had no topo intersection; adjacent segment offsets were used.";

                        RevitNotificationHandler.ShowGeneralMessageDialog("Wall Update Complete", successMessage);
                        Log.Information("Successfully completed WallFollowTopo command");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during Wall follow Topo operation");
                        message = $"Error updating Wall: {ex.Message}";
                        tx.RollBack();
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in WallFollowTopoCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        private sealed class WallSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (!(elem is Wall wall))
                    return false;

                if (wall.IsStackedWall)
                    return false;

                if (wall.WallType?.Kind == WallKind.Curtain)
                    return false;

                return wall.Location is LocationCurve;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private sealed class ToposolidSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
#if REVIT2024_OR_GREATER
                return elem is Toposolid;
#else
                return elem?.Category?.Name == "Topography" || elem?.Category?.Name == "Toposolid";
#endif
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
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

        private static string GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value.ToString() ?? "null";
#else
            return id?.IntegerValue.ToString() ?? "null";
#endif
        }
    }
}
