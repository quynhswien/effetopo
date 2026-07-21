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
    /// Sets elevation on model lines and splines with optional labels and graphic overrides.
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class SetElevationCommand : BaseCommand
    {
        public static readonly string COMMAND_NAME = "SetElevation";

        public override string CommandName => COMMAND_NAME;

        protected override Result ExecuteCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;
                if (doc == null)
                {
                    message = "No active document";
                    return Result.Failed;
                }

                Log.Information("Starting SetElevation command");

                bool useMillimeters = IsProjectUsingMillimeters(doc);
                var savedSettings = SetElevationSettingsService.Instance.Load();
                var dialog = new SetElevationDialog(doc, useMillimeters, savedSettings);
                if (dialog.ShowDialog() != true || dialog.SelectedOptions == null)
                {
                    Log.Information("User cancelled Set Elevation dialog");
                    return Result.Cancelled;
                }

                SetElevationOptions options = dialog.SelectedOptions;
                IList<Reference> pickedRefs;
                try
                {
                    pickedRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new ModelCurveSelectionFilter(),
                        "Select model lines or splines (in order). Press Finish when done.");
                }
                catch (Exception ex) when (IsUserCancel(ex))
                {
                    Log.Information("User cancelled model curve selection");
                    return Result.Cancelled;
                }

                if (pickedRefs == null || pickedRefs.Count == 0)
                {
                    message = "No model lines or splines selected";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                var curves = new List<ModelCurve>();
                foreach (Reference reference in pickedRefs)
                {
                    Element element = doc.GetElement(reference);
                    if (element is ModelCurve modelCurve && IsSupportedCurve(modelCurve))
                        curves.Add(modelCurve);
                }

                if (curves.Count == 0)
                {
                    message = "Selected elements are not model lines or splines";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error", message);
                    return Result.Failed;
                }

                IReadOnlyList<SetElevationLineResult> results;
                using (Transaction tx = new Transaction(doc, "Set Elevation"))
                {
                    tx.Start();
                    try
                    {
                        results = SetElevationService.Instance.Apply(doc, uidoc.ActiveView, curves, options);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Set Elevation operation failed");
                        tx.RollBack();
                        message = ex.Message;
                        RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                        return Result.Failed;
                    }
                }

                int successCount = results.Count(r => r.Success);
                uidoc.Selection.SetElementIds(curves.Select(c => c.Id).ToList());

                string detail = $"Updated {successCount} of {results.Count} curve(s).\n\n";
                for (int i = 0; i < results.Count; i++)
                {
                    SetElevationLineResult result = results[i];
                    string status = result.Success ? result.FormattedElevation : $"Failed: {result.Message}";
                    detail += $"{i + 1}. {status}\n";
                }

                RevitNotificationHandler.ShowGeneralMessageDialog("Set Elevation Complete", detail.TrimEnd());
                Log.Information("SetElevation completed: {Success}/{Total}", successCount, results.Count);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in SetElevationCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        private static bool IsSupportedCurve(ModelCurve modelCurve)
        {
            Curve curve = modelCurve.GeometryCurve;
            return curve is Line || curve is NurbSpline || curve is HermiteSpline || curve is Arc;
        }

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
                FormatOptions lengthFormat = units?.GetFormatOptions(UnitType.UT_Length);
                return lengthFormat != null && lengthFormat.DisplayUnits == DisplayUnitType.DUT_MILLIMETERS;
#endif
            }
            catch
            {
                return false;
            }
        }

        private sealed class ModelCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is ModelCurve modelCurve && IsSupportedCurve(modelCurve);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
