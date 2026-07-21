using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// In-memory draft for Shape by Line: curves accumulate until Commit() on Ok.
    /// </summary>
    internal sealed class ModifyTopoLineDraftSession
    {
        public sealed class LineRecord
        {
            public ElementId CurveElementId;
            public ModifyTopoOptions Options;
        }

        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _baseVertices;
        private readonly List<LineRecord> _lines = new List<LineRecord>();
        private TerrainModifier.CalculateResult _lastCalculated;

        public ModifyTopoLineDraftSession(Document doc, Toposolid toposolid)
        {
            _doc = doc;
            _toposolid = toposolid;
            _baseVertices = ModifyTopoService.Instance.GetVertexSnapshots(toposolid);
            Recalculate();
        }

        public int OriginalPointCount => _baseVertices.Count;
        public int DraftPointCount => _lastCalculated?.Vertices?.Count ?? _baseVertices.Count;
        public int LineCount => _lines.Count;
        public bool HasPendingChanges => _lines.Count > 0;
        public TerrainModifier.CalculateResult LastCalculated => _lastCalculated;

        public ModifyTopoDraftStampResult StageCurves(IEnumerable<ElementId> curveIds, ModifyTopoOptions options)
        {
            if (curveIds == null || options == null)
                throw new ArgumentNullException();

            int pointsBefore = DraftPointCount;
            int staged = 0;

            foreach (ElementId id in curveIds)
            {
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                _lines.Add(new LineRecord
                {
                    CurveElementId = id,
                    Options = CloneOptions(options)
                });
                staged++;
            }

            if (staged == 0)
            {
                return new ModifyTopoDraftStampResult
                {
                    StampIndex = _lines.Count,
                    PointsAdded = 0,
                    VerticesModified = 0,
                    TotalDraftPoints = DraftPointCount,
                    PointsDeltaFromOriginal = DraftPointCount - OriginalPointCount
                };
            }

            Recalculate();
            int pointsAdded = DraftPointCount - pointsBefore;

            return new ModifyTopoDraftStampResult
            {
                StampIndex = _lines.Count,
                PointsAdded = pointsAdded,
                VerticesModified = _lastCalculated?.TotalVerticesModified ?? 0,
                TotalDraftPoints = DraftPointCount,
                PointsDeltaFromOriginal = DraftPointCount - OriginalPointCount
            };
        }

        public void UpdateLiveLineOptions(ModifyTopoOptions liveOptions)
        {
            if (liveOptions == null || _lines.Count == 0)
                return;

            foreach (LineRecord line in _lines)
                ApplyLineOptions(line.Options, liveOptions);

            Recalculate();
        }

        public bool TryUndoLastLine()
        {
            if (_lines.Count == 0)
                return false;

            _lines.RemoveAt(_lines.Count - 1);
            Recalculate();
            return true;
        }

        public ModifyTopoResult Commit()
        {
            if (_lines.Count == 0)
            {
                return new ModifyTopoResult
                {
                    OriginalPointCount = OriginalPointCount,
                    PointsAfterModification = OriginalPointCount,
                    Summary = "No draft line changes to commit."
                };
            }

            Recalculate();
            ModifyTopoResult last;
            using (Transaction tx = new Transaction(_doc, "Shape by Line (commit draft)"))
            {
                tx.Start();
                last = ModifyTopoService.Instance.ApplyCalculatedVertices(
                    _doc, _toposolid, _baseVertices, _lastCalculated.Vertices);
                tx.Commit();
            }

            Log.Information(
                "Line draft committed from preview: {LineCount} curves, {Total} points on Toposolid",
                _lines.Count, last?.PointsAfterModification ?? DraftPointCount);

            _lines.Clear();
            _baseVertices.Clear();
            _baseVertices.AddRange(ModifyTopoService.Instance.GetVertexSnapshots(_toposolid));
            Recalculate();
            return last;
        }

        private void Recalculate()
        {
            var lineDefs = new List<TerrainModifier.LineDefinition>();

            foreach (LineRecord record in _lines)
            {
                Curve curve = GetCurve(record.CurveElementId);
                if (curve == null)
                    continue;

                var samplingOptions = new FloorBoundarySamplingOptions
                {
                    Mode = record.Options.LineSampleMode,
                    SpacingFeet = record.Options.LineSpacingFeet,
                    SegmentsPerCurve = record.Options.LineSegmentsPerCurve
                };

                List<XYZ> samplePoints = CurvePointSampler.Sample(curve, samplingOptions);
                if (samplePoints.Count == 0)
                    continue;

                lineDefs.Add(new TerrainModifier.LineDefinition { SamplePoints = samplePoints });
            }

            _lastCalculated = TerrainModifier.CalculateWithLines(
                _doc, _toposolid, _baseVertices, lineDefs);
        }

        private Curve GetCurve(ElementId elementId)
        {
            if (elementId == null || elementId == ElementId.InvalidElementId)
                return null;

            if (_doc.GetElement(elementId) is not ModelCurve modelCurve)
                return null;

            return modelCurve.GeometryCurve;
        }

        private static void ApplyLineOptions(ModifyTopoOptions target, ModifyTopoOptions live)
        {
            target.LineSampleMode = live.LineSampleMode;
            target.LineSpacingFeet = live.LineSpacingFeet;
            target.LineSegmentsPerCurve = live.LineSegmentsPerCurve;
            target.ShowPreview = live.ShowPreview;
        }

        private static ModifyTopoOptions CloneOptions(ModifyTopoOptions o) => new ModifyTopoOptions
        {
            Tool = o.Tool,
            LineSampleMode = o.LineSampleMode,
            LineSpacingFeet = o.LineSpacingFeet,
            LineSegmentsPerCurve = o.LineSegmentsPerCurve,
            ShowPreview = o.ShowPreview
        };
    }
#endif
}
