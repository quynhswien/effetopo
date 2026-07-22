using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// In-memory draft: stamps accumulate as preview until Commit() on Ok.
    /// Toposolid is not modified until commit.
    /// </summary>
    internal sealed class ModifyTopoDraftSession
    {
        public sealed class StampRecord
        {
            public XYZ Center;
            public ModifyTopoOptions Options;
            public double? PickSurveyElevationFt;
        }

        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoGeometrySurfaceCache _displayTopology;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _baseVertices;
        private readonly List<StampRecord> _stamps = new List<StampRecord>();
        private TerrainModifier.CalculateResult _lastCalculated;

        public ModifyTopoDraftSession(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache displayTopology)
        {
            _doc = doc;
            _toposolid = toposolid;
            _displayTopology = displayTopology;
            _baseVertices = ModifyTopoService.Instance.GetVertexSnapshots(toposolid);
            Recalculate();
        }

        public int OriginalPointCount => _baseVertices.Count;
        public int DraftPointCount => _lastCalculated?.Vertices?.Count ?? _baseVertices.Count;
        public int StampCount => _stamps.Count;
        public bool HasPendingChanges => _stamps.Count > 0;
        public IReadOnlyList<StampRecord> Stamps => _stamps;
        public TerrainModifier.CalculateResult LastCalculated => _lastCalculated;

        public ModifyTopoDraftStampResult StageStamp(
            XYZ center,
            ModifyTopoOptions options,
            double? pickSurveyElevationFt = null)
        {
            if (center == null || options == null)
                throw new ArgumentNullException();

            int pointsBefore = DraftPointCount;
            _stamps.Add(new StampRecord
            {
                Center = center,
                Options = CloneOptions(options),
                PickSurveyElevationFt = pickSurveyElevationFt
            });

            Recalculate();
            int pointsAdded = DraftPointCount - pointsBefore;

            return new ModifyTopoDraftStampResult
            {
                StampIndex = _stamps.Count,
                PointsAdded = pointsAdded,
                VerticesModified = _lastCalculated?.TotalVerticesModified ?? 0,
                TotalDraftPoints = DraftPointCount,
                PointsDeltaFromOriginal = DraftPointCount - OriginalPointCount
            };
        }

        public void UpdateLiveShapeOptions(ModifyTopoOptions liveOptions)
        {
            if (liveOptions == null || _stamps.Count == 0)
                return;

            foreach (StampRecord stamp in _stamps)
                ApplyShapeOptions(stamp.Options, liveOptions);

            Recalculate();
        }

        public bool TryUndoLastStamp()
        {
            if (_stamps.Count == 0)
                return false;

            _stamps.RemoveAt(_stamps.Count - 1);
            Recalculate();
            return true;
        }

        public StampRecord GetLastStamp() =>
            _stamps.Count > 0 ? _stamps[_stamps.Count - 1] : null;

        public IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> GetBaseVertices() =>
            _baseVertices;

        public IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> GetWorkingVertices() =>
            _lastCalculated?.Vertices ?? _baseVertices;

        public ModifyTopoResult Commit()
        {
            if (_stamps.Count == 0)
            {
                return new ModifyTopoResult
                {
                    OriginalPointCount = OriginalPointCount,
                    PointsAfterModification = OriginalPointCount,
                    Summary = "No draft changes to commit."
                };
            }

            Recalculate();
            ModifyTopoResult last;
            using (Transaction tx = new Transaction(_doc, "Shape by Point (commit draft)"))
            {
                tx.Start();
            StampRecord stamp = _stamps[_stamps.Count - 1];
                last = ModifyTopoService.Instance.ApplyCalculatedVertices(
                    _doc, _toposolid, _baseVertices, _lastCalculated.Vertices,
                    logResult: true,
                    stampPickCenter: stamp?.Center,
                    stampPickSurveyFt: stamp?.PickSurveyElevationFt,
                    stampGainFeet: stamp?.Options?.ShapeDeltaFeet ?? 0);
                tx.Commit();
            }

            Log.Information(
                "Draft committed from preview mesh: {StampCount} stamps, {Total} points on Toposolid",
                _stamps.Count, last?.PointsAfterModification ?? DraftPointCount);

            _stamps.Clear();
            _baseVertices.Clear();
            _baseVertices.AddRange(ModifyTopoService.Instance.GetVertexSnapshots(_toposolid));
            Recalculate();
            return last;
        }

        private void Recalculate()
        {
            var stampDefs = _stamps
                .Select(s => new TerrainModifier.StampDefinition
                {
                    Center = s.Center,
                    Options = s.Options,
                    PickSurveyElevationFt = s.PickSurveyElevationFt
                })
                .ToList();

            _lastCalculated = TerrainModifier.Calculate(
                _doc, _toposolid, _baseVertices, stampDefs, _displayTopology);
        }

        private static void ApplyShapeOptions(ModifyTopoOptions target, ModifyTopoOptions live)
        {
            target.ShapeRadiusFeet = live.ShapeRadiusFeet;
            target.ShapeDeltaFeet = live.ShapeDeltaFeet;
            target.ShapeFalloff = live.ShapeFalloff;
            target.ShapePointDensity = live.ShapePointDensity;
            target.ShowPreview = live.ShowPreview;
        }

        private static ModifyTopoOptions CloneOptions(ModifyTopoOptions o) => new ModifyTopoOptions
        {
            Tool = o.Tool,
            ShapeRadiusFeet = o.ShapeRadiusFeet,
            ShapeDeltaFeet = o.ShapeDeltaFeet,
            ShapeFalloff = o.ShapeFalloff,
            ShapePointDensity = o.ShapePointDensity,
            ShowPreview = o.ShowPreview
        };
    }

    internal sealed class ModifyTopoDraftStampResult
    {
        public int StampIndex;
        public int PointsAdded;
        public int VerticesModified;
        public int TotalDraftPoints;
        public int PointsDeltaFromOriginal;
    }
#endif
}
