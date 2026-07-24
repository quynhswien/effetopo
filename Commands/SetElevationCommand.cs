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
    /// Sets elevation on model lines and splines interactively — sequential Set or Match from a source.
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

                return dialog.SelectedMode == SetElevationMode.Match
                    ? RunMatchElevation(uidoc, doc, activeView, options, projectData)
                    : RunSetElevation(uidoc, doc, activeView, options, projectData);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in SetElevationCommand");
                message = ex.Message;
                RevitNotificationHandler.ShowGeneralMessageDialog("Error", message);
                return Result.Failed;
            }
        }

        private static Result RunSetElevation(
            UIDocument uidoc,
            Document doc,
            View activeView,
            SetElevationOptions options,
            SetElevationProjectData projectData)
        {
            int appliedCount = 0;
            int sequenceIndex = projectData.NextSequenceIndex;

            while (true)
            {
                string nextElevationHint = FormatElevation(doc,
                    options.StartElevationFeet + sequenceIndex * options.IncrementFeet);
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

                if (!TryGetSupportedModelCurve(doc, pickedRef, out ModelCurve modelCurve))
                    continue;

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
                    uidoc.Selection.SetElementIds(new List<ElementId> { modelCurve.Id });
                    uidoc.RefreshActiveView();
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    RevitNotificationHandler.ShowGeneralMessageDialog("Set Elevation", result.Message);
                }
            }

            return FinishWithSummary(activeView, options, projectData, appliedCount, "Set Elevation Complete");
        }

        private static Result RunMatchElevation(
            UIDocument uidoc,
            Document doc,
            View activeView,
            SetElevationOptions options,
            SetElevationProjectData projectData)
        {
            Reference sourceRef;
            try
            {
                sourceRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ModelCurveSelectionFilter(),
                    "Match Elevation: pick the source model line or spline. Press Esc to cancel.");
            }
            catch (Exception ex) when (IsUserCancel(ex))
            {
                Log.Information("Match Elevation cancelled while picking source");
                return Result.Cancelled;
            }

            if (!TryGetSupportedModelCurve(doc, sourceRef, out ModelCurve sourceCurve))
                return Result.Failed;

            if (!SetElevationService.Instance.TryGetCurveDisplayElevation(
                    doc, activeView, sourceCurve, options.ElevationBase, projectData,
                    out double sourceElevationFeet))
            {
                RevitNotificationHandler.ShowGeneralMessageDialog("Match Elevation",
                    "Could not read elevation from the source line.");
                return Result.Failed;
            }

            long sourceId = GetElementIdValue(sourceCurve.Id);
            SetElevationLineRecord? sourceRecord =
                SetElevationDataService.Instance.FindRecord(projectData, sourceId);
            int sequenceIndex = sourceRecord?.SequenceOrder ?? projectData.NextSequenceIndex;
            string elevationHint = FormatElevation(doc, sourceElevationFeet);

            uidoc.Selection.SetElementIds(new List<ElementId> { sourceCurve.Id });
            uidoc.RefreshActiveView();

            IList<Reference> targetRefs;
            try
            {
                targetRefs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new ModelCurveSelectionFilter(excludeId: sourceCurve.Id),
                    $"Match Elevation: select one or more target lines (source elevation: {elevationHint}), then Finish.");
            }
            catch (Exception ex) when (IsUserCancel(ex))
            {
                Log.Information("Match Elevation cancelled while picking targets");
                return Result.Cancelled;
            }

            if (targetRefs == null || targetRefs.Count == 0)
            {
                RevitNotificationHandler.ShowGeneralMessageDialog("Match Elevation",
                    "No target lines were selected.");
                return Result.Cancelled;
            }

            var targets = new List<ModelCurve>();
            var seenIds = new HashSet<long> { sourceId };
            foreach (Reference targetRef in targetRefs)
            {
                Element element = doc.GetElement(targetRef);
                if (!(element is ModelCurve targetCurve) || !IsSupportedCurve(targetCurve))
                    continue;

                long targetId = GetElementIdValue(targetCurve.Id);
                if (!seenIds.Add(targetId))
                    continue;

                targets.Add(targetCurve);
            }

            if (targets.Count == 0)
            {
                RevitNotificationHandler.ShowGeneralMessageDialog("Match Elevation",
                    "No valid target model lines or splines were selected.");
                return Result.Failed;
            }

            int appliedCount = 0;
            var failedMessages = new List<string>();

            using (Transaction tx = new Transaction(doc, "Match Elevation"))
            {
                tx.Start();
                try
                {
                    foreach (ModelCurve targetCurve in targets)
                    {
                        SetElevationLineResult result = SetElevationService.Instance.ApplyMatch(
                            doc, activeView, targetCurve, options, projectData,
                            sourceElevationFeet, sequenceIndex);

                        if (result.Success)
                            appliedCount++;
                        else if (!string.IsNullOrEmpty(result.Message))
                            failedMessages.Add($"Curve {result.ElementId}: {result.Message}");
                    }

                    if (appliedCount > 0)
                    {
                        SetElevationDataService.Instance.Save(
                            doc, projectData, includeProjectMetadata: true, includeLocalFile: false);
                        doc.Regenerate();
                        tx.Commit();
                    }
                    else
                    {
                        tx.RollBack();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Match Elevation batch failed");
                    if (tx.HasStarted())
                        tx.RollBack();
                    RevitNotificationHandler.ShowGeneralMessageDialog("Error",
                        $"Failed to match elevation:\n{ex.Message}");
                    return Result.Failed;
                }
            }

            if (appliedCount > 0)
            {
                SetElevationDataService.Instance.Save(
                    doc, projectData, includeProjectMetadata: false, includeLocalFile: true);
                uidoc.Selection.SetElementIds(targets.Select(t => t.Id).ToList());
                uidoc.RefreshActiveView();
            }

            if (failedMessages.Count > 0)
            {
                string detail = string.Join("\n", failedMessages.Take(8));
                if (failedMessages.Count > 8)
                    detail += $"\n…and {failedMessages.Count - 8} more.";
                RevitNotificationHandler.ShowGeneralMessageDialog("Match Elevation",
                    $"Some targets could not be updated:\n{detail}");
            }

            return FinishWithSummary(activeView, options, projectData, appliedCount, "Match Elevation Complete");
        }

        private static Result FinishWithSummary(
            View activeView,
            SetElevationOptions options,
            SetElevationProjectData projectData,
            int appliedCount,
            string title)
        {
            if (appliedCount == 0)
            {
                Log.Information("{Title} finished with no changes", title);
                return Result.Cancelled;
            }

            string summary = $"Updated elevation on {appliedCount} curve(s).\n" +
                $"Total linked assignments in project: {projectData.Lines.Count}";

            if (options.AddLabel && activeView.ViewType == ViewType.ThreeD)
            {
                summary += "\n\nElevation labels were placed in the matching floor plan view " +
                           "(Text Notes are not visible in 3D view). Open the corresponding plan to see them.";
            }

            RevitNotificationHandler.ShowGeneralMessageDialog(title, summary);
            Log.Information("{Title}: {Applied} applied, {Total} total linked",
                title, appliedCount, projectData.Lines.Count);
            return Result.Succeeded;
        }

        private static bool TryGetSupportedModelCurve(Document doc, Reference pickedRef, out ModelCurve modelCurve)
        {
            modelCurve = null;
            Element element = doc.GetElement(pickedRef);
            if (element is ModelCurve curve && IsSupportedCurve(curve))
            {
                modelCurve = curve;
                return true;
            }

            RevitNotificationHandler.ShowGeneralMessageDialog("Selection Error",
                "Selected element is not a model line or spline.");
            return false;
        }

        private static void EnsureSequenceIndex(SetElevationProjectData projectData)
        {
            projectData.Lines ??= new List<SetElevationLineRecord>();

            if (projectData.Lines.Count == 0)
            {
                projectData.NextSequenceIndex = 0;
                return;
            }

            int maxOrder = projectData.Lines.Max(line => line.SequenceOrder);
            if (projectData.NextSequenceIndex <= maxOrder)
                projectData.NextSequenceIndex = maxOrder + 1;
        }

        private static string FormatElevation(Document doc, double elevationFeet)
        {
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

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value ?? -1;
#else
            return id?.IntegerValue ?? -1;
#endif
        }

        private sealed class ModelCurveSelectionFilter : ISelectionFilter
        {
            private readonly ElementId _excludeId;

            public ModelCurveSelectionFilter(ElementId excludeId = null)
            {
                _excludeId = excludeId;
            }

            public bool AllowElement(Element elem)
            {
                if (_excludeId != null && elem?.Id == _excludeId)
                    return false;

                return elem is ModelCurve modelCurve && IsSupportedCurve(modelCurve);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
