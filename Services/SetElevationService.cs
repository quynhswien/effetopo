using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Applies elevation values to model lines and splines, with linked labels and persistence.
    /// </summary>
    public class SetElevationService
    {
        private static SetElevationService _instance;
        private static readonly object _lock = new object();

        public static SetElevationService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SetElevationService();
                    }
                }
                return _instance;
            }
        }

        private SetElevationService() { }

        public SetElevationLineResult ApplySingle(
            Document doc,
            View view,
            ModelCurve modelCurve,
            SetElevationOptions options,
            SetElevationProjectData projectData,
            int sequenceIndex)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (modelCurve == null) throw new ArgumentNullException(nameof(modelCurve));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (projectData == null) throw new ArgumentNullException(nameof(projectData));

            double elevationFeet = options.StartElevationFeet + sequenceIndex * options.IncrementFeet;
            var elevationHelper = new ElevationReferenceHelper(doc, view, options.ElevationBase);
            var lineResult = new SetElevationLineResult
            {
                ElementId = GetElementIdValue(modelCurve.Id),
                SequenceOrder = sequenceIndex,
                DisplayElevation = elevationFeet
            };

            try
            {
                if (!TrySetCurveElevation(modelCurve, elevationHelper, elevationFeet, out XYZ labelPoint))
                {
                    lineResult.Success = false;
                    lineResult.Message = "Could not update curve geometry.";
                    return lineResult;
                }

                lineResult.FormattedElevation = FormatElevation(doc, elevationFeet);
                ApplyGraphicOverride(view, modelCurve.Id, options.OverrideColor);

                ElementId textTypeId = ResolveTextTypeId(doc, options);
                long textNoteId = 0;

                if (options.AddLabel && textTypeId != ElementId.InvalidElementId)
                {
                    try
                    {
                        textNoteId = UpdateOrCreateLabel(
                            doc, view, projectData, modelCurve.Id, labelPoint,
                            lineResult.FormattedElevation, textTypeId);
                    }
                    catch (Exception labelEx)
                    {
                        Log.Warning(labelEx, "Could not create elevation label on curve {Id}", lineResult.ElementId);
                    }
                }

                lineResult.TextNoteElementId = textNoteId;
                UpsertRecord(projectData, modelCurve.Id, sequenceIndex, elevationFeet,
                    lineResult.FormattedElevation, textNoteId, options);
                projectData.NextSequenceIndex = sequenceIndex + 1;

                lineResult.Success = true;
            }
            catch (Exception ex)
            {
                lineResult.Success = false;
                lineResult.Message = ex.Message;
                Log.Warning(ex, "Failed to set elevation on model curve {Id}", lineResult.ElementId);
            }

            return lineResult;
        }

        private static void UpsertRecord(
            SetElevationProjectData projectData,
            ElementId curveId,
            int sequenceIndex,
            double elevationFeet,
            string formattedElevation,
            long textNoteId,
            SetElevationOptions options)
        {
            long curveElementId = GetElementIdValue(curveId);
            SetElevationLineRecord? existing = SetElevationDataService.Instance.FindRecord(projectData, curveElementId);

            if (existing != null)
            {
                existing.SequenceOrder = sequenceIndex;
                existing.ElevationFeet = elevationFeet;
                existing.FormattedElevation = formattedElevation;
                existing.TextNoteElementId = textNoteId;
                existing.AssignedAt = DateTime.UtcNow;
            }
            else
            {
                projectData.Lines ??= new List<SetElevationLineRecord>();
                projectData.Lines.Add(new SetElevationLineRecord
                {
                    SequenceOrder = sequenceIndex,
                    CurveElementId = curveElementId,
                    TextNoteElementId = textNoteId,
                    ElevationFeet = elevationFeet,
                    FormattedElevation = formattedElevation,
                    AssignedAt = DateTime.UtcNow
                });
            }

            projectData.ElevationBase = options.ElevationBase;
            projectData.StartElevationFeet = options.StartElevationFeet;
            projectData.IncrementFeet = options.IncrementFeet;
            projectData.TextTypeId = options.TextTypeId;
        }

        private static long UpdateOrCreateLabel(
            Document doc,
            View view,
            SetElevationProjectData projectData,
            ElementId curveId,
            XYZ labelPoint,
            string text,
            ElementId textTypeId)
        {
            long curveElementId = GetElementIdValue(curveId);
            SetElevationLineRecord? existing = SetElevationDataService.Instance.FindRecord(projectData, curveElementId);

            if (existing?.TextNoteElementId > 0)
            {
                ElementId noteId = CreateElementId(existing.TextNoteElementId);
                if (doc.GetElement(noteId) is TextNote existingNote)
                {
                    existingNote.Text = text;
                    MoveTextNote(existingNote, labelPoint);
                    return existing.TextNoteElementId;
                }
            }

            View labelView = ResolveLabelView(doc, view);
            if (labelView == null)
                throw new InvalidOperationException("No suitable view found for elevation labels.");

            TextNote created = TextNote.Create(doc, labelView.Id, labelPoint, text, textTypeId);
            if (created == null)
                throw new InvalidOperationException("Revit could not create the elevation text note.");

            return GetElementIdValue(created.Id);
        }

        private static void MoveTextNote(TextNote textNote, XYZ target)
        {
            LocationPoint location = textNote.Location as LocationPoint;
            if (location == null) return;

            XYZ current = location.Point;
            XYZ delta = target - current;
            if (delta.GetLength() < 1e-6) return;

            location.Move(delta);
        }

        private static ElementId ResolveTextTypeId(Document doc, SetElevationOptions options)
        {
            if (options.TextTypeId > 0)
            {
                ElementId id = CreateElementId(options.TextTypeId);
                if (doc.GetElement(id) is TextNoteType)
                    return id;
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
        }

        private static bool TrySetCurveElevation(
            ModelCurve modelCurve,
            ElevationReferenceHelper elevationHelper,
            double displayElevationFeet,
            out XYZ labelPoint)
        {
            labelPoint = XYZ.Zero;
            LocationCurve location = modelCurve.Location as LocationCurve;
            if (location?.Curve == null)
                return false;

            if (modelCurve.Pinned)
            {
                Log.Warning("Model curve {Id} is pinned and cannot be moved.", GetElementIdValue(modelCurve.Id));
                return false;
            }

            Curve curve = location.Curve;

            try
            {
                if (TrySetLineElevation(location, curve, elevationHelper, displayElevationFeet, out labelPoint))
                    return true;

                XYZ mid = curve.Evaluate(0.5, true);
                if (mid == null)
                    return false;

                double targetModelZ = elevationHelper.DisplayElevationToModelZ(mid.X, mid.Y, displayElevationFeet);
                double deltaZ = targetModelZ - mid.Z;

                if (Math.Abs(deltaZ) < 1e-9)
                {
                    labelPoint = mid;
                    return true;
                }

                Transform move = Transform.CreateTranslation(new XYZ(0, 0, deltaZ));
                Curve movedCurve = curve.CreateTransformed(move);
                if (movedCurve != null)
                {
                    location.Curve = movedCurve;
                    labelPoint = movedCurve.Evaluate(0.5, true);
                    Log.Debug(
                        "Set elevation on curve {Id}: moved {DeltaZ} ft via transformed curve",
                        GetElementIdValue(modelCurve.Id), deltaZ);
                    return true;
                }

                ElementTransformUtils.MoveElement(modelCurve.Document, modelCurve.Id, new XYZ(0, 0, deltaZ));
                Curve updated = location.Curve;
                labelPoint = updated?.Evaluate(0.5, true) ?? new XYZ(mid.X, mid.Y, targetModelZ);
                Log.Debug(
                    "Set elevation on curve {Id}: moved {DeltaZ} ft via ElementTransformUtils",
                    GetElementIdValue(modelCurve.Id), deltaZ);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to set elevation on model curve {Id}", GetElementIdValue(modelCurve.Id));
                return false;
            }
        }

        private static bool TrySetLineElevation(
            LocationCurve location,
            Curve curve,
            ElevationReferenceHelper elevationHelper,
            double displayElevationFeet,
            out XYZ labelPoint)
        {
            labelPoint = XYZ.Zero;
            if (curve is not Line)
                return false;

            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            double z0 = elevationHelper.DisplayElevationToModelZ(p0.X, p0.Y, displayElevationFeet);
            double z1 = elevationHelper.DisplayElevationToModelZ(p1.X, p1.Y, displayElevationFeet);

            location.Curve = Line.CreateBound(
                new XYZ(p0.X, p0.Y, z0),
                new XYZ(p1.X, p1.Y, z1));

            labelPoint = new XYZ(
                (p0.X + p1.X) * 0.5,
                (p0.Y + p1.Y) * 0.5,
                (z0 + z1) * 0.5);

            return true;
        }

        private static void ApplyGraphicOverride(View view, ElementId elementId, Color color)
        {
            if (view == null || elementId == null || elementId == ElementId.InvalidElementId)
                return;

            Color safeColor = color ?? new Color(255, 128, 0);
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(safeColor);
            ogs.SetCutLineColor(safeColor);
            ogs.SetSurfaceForegroundPatternColor(safeColor);
            view.SetElementOverrides(elementId, ogs);
        }

        private static View ResolveLabelView(Document doc, View activeView)
        {
            if (activeView != null && activeView.CanBePrinted && !activeView.IsTemplate)
            {
                if (activeView.ViewType == ViewType.FloorPlan ||
                    activeView.ViewType == ViewType.CeilingPlan ||
                    activeView.ViewType == ViewType.EngineeringPlan ||
                    activeView.ViewType == ViewType.Section ||
                    activeView.ViewType == ViewType.Elevation)
                {
                    return activeView;
                }
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
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

        private static ElementId CreateElementId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
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
