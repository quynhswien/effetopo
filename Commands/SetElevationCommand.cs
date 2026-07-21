using System;
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
    /// Sets elevation on model lines and splines interactively — each click applies the next step.
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
                SetElevationDataService.Instance.EnsureSchemaRegistered();

                View activeView = uidoc.ActiveView ?? doc.ActiveView;
                if (activeView == null)
                {
                    message = "No active view. Open a plan, section, or 3D view before setting elevation.";
                    RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                    return Result.Failed;
                }

                SetElevationProjectData projectData = SetElevationDataService.Instance.Load(doc);
                EnsureSequenceIndex(projectData);

                int appliedCount = 0;
                int sequenceIndex = projectData.NextSequenceIndex;

                while (true)
                {
                    string nextElevationHint = FormatNextElevation(doc, options, sequenceIndex);
                    Reference pickedRef;
                    try
                    {
                        pickedRef = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new ModelCurveSelectionFilter(),
                            $"Click a model line or spline. Next elevation: {nextElevationHint}. Press Esc to finish.");
                    }
                    catch (Exception ex) when (IsUserCancel(ex))
                    {
                        break;
                    }

                    if (pickedRef == null)
                        break;

                    Element element = doc.GetElement(pickedRef);
                    if (!(element is ModelCurve modelCurve) || !IsSupportedCurve(modelCurve))
                    {
                        RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error",
                            "Selected element is not a model line or spline.");
                        continue;
                    }

                    SetElevationLineResult result;
                    using (Transaction tx = new Transaction(doc, "Set Elevation"))
                    {
                        tx.Start();
                        try
                        {
                            result = SetElevationService.Instance.ApplySingle(
                                doc, activeView, modelCurve, options, projectData, sequenceIndex);
                            SetElevationDataService.Instance.Save(
                                doc, projectData, includeProjectMetadata: true, includeLocalFile: false);
                            doc.Regenerate();
                            tx.Commit();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Set Elevation click failed");
                            if (tx.HasStarted())
                                tx.RollBack();
                            RevitNotificationHandler.ShowGeneralMessageDialog("Error",
                                $"Failed to set elevation:\n{ex.Message}");
                            continue;
                        }
                    }

                    SetElevationDataService.Instance.Save(
                        doc, projectData, includeProjectMetadata: false, includeLocalFile: true);

                    if (result.Success)
                    {
                        appliedCount++;
                        sequenceIndex++;
                        uidoc.Selection.SetElementIds(new System.Collections.Generic.List<ElementId> { modelCurve.Id });
                        uidoc.RefreshActiveView();
                    }
                    else if (!string.IsNullOrEmpty(result.Message))
                    {
                        RevitNotificationHandler.ShowGeneralMessageDialog("Set Elevation", result.Message);
                    }
                }

                if (appliedCount == 0)
                {
                    Log.Information("SetElevation finished with no changes");
                    return Result.Cancelled;
                }

                string summary = $"Set elevation on {appliedCount} curve(s).\n" +
                    $"Total linked assignments in project: {projectData.Lines.Count}";
                RevitNotificationHandler.ShowGeneralMessageDialog("Set Elevation Complete", summary);
                Log.Information("SetElevation completed: {Applied} applied, {Total} total linked",
                    appliedCount, projectData.Lines.Count);
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

        private static void EnsureSequenceIndex(SetElevationProjectData projectData)
        {
            projectData.Lines ??= new System.Collections.Generic.List<SetElevationLineRecord>();

            if (projectData.Lines.Count == 0)
            {
                projectData.NextSequenceIndex = 0;
                return;
            }

            int maxOrder = projectData.Lines.Max(line => line.SequenceOrder);
            if (projectData.NextSequenceIndex <= maxOrder)
                projectData.NextSequenceIndex = maxOrder + 1;
        }

        private static string FormatNextElevation(Document doc, SetElevationOptions options, int sequenceIndex)
        {
            double elevationFeet = options.StartElevationFeet + sequenceIndex * options.IncrementFeet;
            try
            {
                Units units = doc.GetUnits();
#if REVIT2024_OR_GREATER
                return UnitFormatUtils.Format(units, SpecTypeId.Length, elevationFeet, false);
#else
                return UnitFormatUtils.Format(units, UnitType.UT_Length, elevationFeet, false, false);
#endif
            }
            catch
            {
                return elevationFeet.ToString("F3");
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
