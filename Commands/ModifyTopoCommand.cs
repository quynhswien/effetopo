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
    /// Sculpt and refine Toposolid surfaces: inflate, mesh control, shape by point, smooth.
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class ModifyTopoCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "ModifyTopo";

        public override string CommandName => COMMAND_NAME;

        protected override Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
#if !REVIT2024_OR_GREATER
            message = "Toposolid elements are only available in Revit 2024 and later";
            RevitNotificationHandler.ShowGeneralMessageDialog("Version Error", message);
            return Result.Failed;
#else
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc.Document;
                if (doc == null)
                {
                    message = "No active document";
                    return Result.Failed;
                }

                Log.Information("Starting ModifyTopo command");

                Toposolid toposolid = PickToposolid(uidoc, ref message);
                if (toposolid == null)
                    return message.Contains("cancel") ? Result.Cancelled : Result.Failed;

                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var settingsService = ModifyTopoSettingsService.Instance;
                var savedSettings = settingsService.Load();
                var topoService = ModifyTopoService.Instance;

                int originalCount = topoService.CountSlabShapeVertices(toposolid);
                int currentCount = originalCount;

                while (true)
                {
                    var dialog = new ModifyTopoDialog(useMillimeters, originalCount, currentCount, savedSettings);
                    ModifyTopoOptions options;
                    bool closeAfter;

                    using (var preview = new ModifyTopoPreviewCoordinator(commandData.Application, toposolid, dialog))
                    {
                        preview.Start();
                        if (dialog.ShowModelessAndWait() != true || dialog.SelectedOptions == null)
                        {
                            Log.Information("User cancelled Modify Topo dialog");
                            return Result.Cancelled;
                        }
                        options = dialog.SelectedOptions;
                        closeAfter = dialog.CloseAfterAction;
                    }

                    savedSettings = settingsService.Load();

                    XYZ centerPoint = null;
                    if (options.Tool == ModifyTopoTool.ShapeByPoint)
                    {
                        var picker = new ModifyTopoShapePointPicker(commandData.Application, toposolid, options);
                        centerPoint = picker.Pick();
                        if (centerPoint == null)
                            return Result.Cancelled;
                    }
                    else if (options.Tool == ModifyTopoTool.InflateSurface)
                    {
                        centerPoint = PickPointOnToposolid(uidoc);
                        if (centerPoint == null)
                            return Result.Cancelled;
                    }

                    ModifyTopoResult result;
                    using (Transaction tx = new Transaction(doc, "Modify Toposolid"))
                    {
                        tx.Start();
                        try
                        {
                            result = topoService.Apply(doc, toposolid, options, centerPoint);
                            tx.Commit();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Modify Topo operation failed");
                            tx.RollBack();
                            message = ex.Message;
                            RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                            return Result.Failed;
                        }
                    }

                    currentCount = result.PointsAfterModification;
                    string detail = $"{result.Summary}\n\n" +
                        $"Original points: {originalCount}\n" +
                        $"Current points: {currentCount}";

                    if (closeAfter)
                    {
                        RevitNotificationHandler.ShowGeneralMessageDialog("Modify Toposolid Complete", detail);
                        break;
                    }

                    RevitNotificationHandler.ShowGeneralMessageDialog("Changes Applied", detail);
                }

                uidoc.Selection.SetElementIds(new List<ElementId> { toposolid.Id });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in ModifyTopoCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
#endif
        }

#if REVIT2024_OR_GREATER
        private static Toposolid PickToposolid(UIDocument uidoc, ref string message)
        {
            try
            {
                Reference topoRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ToposolidSelectionFilter(),
                    "Select the Toposolid to modify");
                if (topoRef == null)
                {
                    message = "Please select a Toposolid";
                    return null;
                }

                Element elem = uidoc.Document.GetElement(topoRef);
                if (elem is Toposolid toposolid)
                    return toposolid;

                message = "Selected element is not a Toposolid";
                RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                return null;
            }
            catch (Exception ex) when (IsUserCancel(ex))
            {
                message = "cancelled";
                return null;
            }
        }

        private static XYZ PickPointOnToposolid(UIDocument uidoc)
        {
            try
            {
                return uidoc.Selection.PickPoint(
                    ObjectSnapTypes.Nearest | ObjectSnapTypes.Intersections,
                    "Pick a point on the Toposolid surface (center / control point)");
            }
            catch (Exception ex) when (IsUserCancel(ex))
            {
                Log.Information("User cancelled point pick");
                return null;
            }
        }
#endif

        private static bool IsUserCancel(Exception ex) =>
            ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            ex.GetType().Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase);

        private class ToposolidSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
#if REVIT2024_OR_GREATER
                return elem is Toposolid;
#else
                return false;
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
                return false;
#endif
            }
            catch
            {
                return false;
            }
        }
    }
}
