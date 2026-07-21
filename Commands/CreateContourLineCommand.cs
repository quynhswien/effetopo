using System;
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
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class CreateContourLineCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "CreateContourLine";

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
                Document doc = uidoc?.Document;
                if (doc == null)
                {
                    message = "No active document";
                    return Result.Failed;
                }

                Log.Information("Starting CreateContourLine command");

                Toposolid toposolid = PickToposolid(uidoc, ref message);
                if (toposolid == null)
                    return message.Contains("cancel") ? Result.Cancelled : Result.Failed;

                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var savedSettings = CreateContourLineSettingsService.Instance.Load();
                var dialog = new CreateContourLineDialog(doc, useMillimeters, savedSettings);

                double? suggestedInterval = CreateContourLineService.TryGetSuggestedIntervalFeet(toposolid);
                if (suggestedInterval.HasValue)
                    dialog.ApplySuggestedInterval(suggestedInterval.Value);

                double? suggestedMajorInterval = CreateContourLineService.TryGetSuggestedMajorIntervalFeet(toposolid);
                if (suggestedMajorInterval.HasValue)
                    dialog.ApplySuggestedMajorInterval(suggestedMajorInterval.Value);

                if (toposolid.LevelId != null && toposolid.LevelId != ElementId.InvalidElementId)
                    dialog.ApplySuggestedLevel(GetElementIdValue(toposolid.LevelId));

                if (dialog.ShowDialog() != true || dialog.SelectedOptions == null)
                    return Result.Cancelled;

                CreateContourLineResult result;
                using (Transaction tx = new Transaction(doc, "Create Contour Lines"))
                {
                    tx.Start();
                    try
                    {
                        result = CreateContourLineService.Instance.CreateFromToposolid(
                            doc, toposolid, dialog.SelectedOptions);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Create contour lines failed");
                        tx.RollBack();
                        message = ex.Message;
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }

                RevitNotificationHandler.ShowGeneralMessageDialog(
                    "Create Contour Line Complete",
                    result.Summary);

                Log.Information("CreateContourLine completed: {Summary}", result.Summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in CreateContourLineCommand");
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
                    "Select the Toposolid to create contour lines from");
                if (topoRef == null)
                {
                    message = "Please select a Toposolid";
                    return null;
                }

                Element element = uidoc.Document.GetElement(topoRef);
                if (element is Toposolid toposolid)
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

        private sealed class ToposolidSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Toposolid;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
#endif

        private static bool IsUserCancel(Exception ex) =>
            ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            ex.GetType().Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase);

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

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value ?? -1;
#else
            return id?.IntegerValue ?? -1;
#endif
        }
    }
}
