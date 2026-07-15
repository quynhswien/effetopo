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
            public List<XYZ> PreviewPoints = new List<XYZ>();
            public int PointsAdded;
            public int VerticesModified;
        }

        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _baseVertices;
        private List<ModifyTopoService.SculptVertexSnapshot> _workingVertices;
        private readonly List<StampRecord> _stamps = new List<StampRecord>();

        public ModifyTopoDraftSession(Document doc, Toposolid toposolid)
        {
            _doc = doc;
            _toposolid = toposolid;
            _baseVertices = ModifyTopoService.Instance.GetVertexSnapshots(toposolid);
            _workingVertices = CloneVertices(_baseVertices);
        }

        public int OriginalPointCount => _baseVertices.Count;
        public int DraftPointCount => _workingVertices.Count;
        public int StampCount => _stamps.Count;
        public bool HasPendingChanges => _stamps.Count > 0;
        public IReadOnlyList<StampRecord> Stamps => _stamps;

        public ModifyTopoDraftStampResult StageStamp(XYZ center, ModifyTopoOptions options)
        {
            if (center == null || options == null)
                throw new ArgumentNullException();

            int pointsBefore = _workingVertices.Count;
            var simulate = ModifyTopoService.SimulateShapeByPoint(
                _doc, _toposolid, _workingVertices, center, options);
            _workingVertices = simulate.Vertices;

            var previewPoints = ModifyTopoService.ComputeShapeByPointStampPoints(
                center, options, _workingVertices, _doc, _toposolid, null, previewWithGain: true);

            var record = new StampRecord
            {
                Center = center,
                Options = CloneOptions(options),
                PreviewPoints = previewPoints,
                PointsAdded = simulate.PointsAdded,
                VerticesModified = simulate.VerticesModified
            };
            _stamps.Add(record);

            return new ModifyTopoDraftStampResult
            {
                StampIndex = _stamps.Count,
                PointsAdded = simulate.PointsAdded,
                VerticesModified = simulate.VerticesModified,
                TotalDraftPoints = _workingVertices.Count,
                PointsDeltaFromOriginal = _workingVertices.Count - _baseVertices.Count
            };
        }

        public bool TryUndoLastStamp()
        {
            if (_stamps.Count == 0)
                return false;

            _stamps.RemoveAt(_stamps.Count - 1);
            RebuildWorkingFromBase();
            return true;
        }

        public List<XYZ> GetAllPreviewPoints()
        {
            var all = new List<XYZ>();
            foreach (StampRecord stamp in _stamps)
            {
                if (stamp.PreviewPoints == null) continue;
                all.AddRange(stamp.PreviewPoints);
            }
            return all;
        }

        public StampRecord GetLastStamp() =>
            _stamps.Count > 0 ? _stamps[_stamps.Count - 1] : null;

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

            ModifyTopoResult last = null;
            using (Transaction tx = new Transaction(_doc, "Shape by Point (commit draft)"))
            {
                tx.Start();
                foreach (StampRecord stamp in _stamps)
                {
                    last = ModifyTopoService.Instance.Apply(
                        _doc, _toposolid, stamp.Options, stamp.Center);
                }
                tx.Commit();
            }

            Log.Information(
                "Draft committed: {StampCount} stamps, {Total} points on Toposolid",
                _stamps.Count, last?.PointsAfterModification ?? DraftPointCount);

            _stamps.Clear();
            _baseVertices.Clear();
            _baseVertices.AddRange(ModifyTopoService.Instance.GetVertexSnapshots(_toposolid));
            _workingVertices = CloneVertices(_baseVertices);
            return last;
        }

        private void RebuildWorkingFromBase()
        {
            _workingVertices = CloneVertices(_baseVertices);
            foreach (StampRecord stamp in _stamps)
            {
                var simulate = ModifyTopoService.SimulateShapeByPoint(
                    _doc, _toposolid, _workingVertices, stamp.Center, stamp.Options);
                _workingVertices = simulate.Vertices;

                stamp.PreviewPoints = ModifyTopoService.ComputeShapeByPointStampPoints(
                    stamp.Center, stamp.Options, _workingVertices,
                    _doc, _toposolid, null, previewWithGain: true);
            }
        }

        private static List<ModifyTopoService.SculptVertexSnapshot> CloneVertices(
            IList<ModifyTopoService.SculptVertexSnapshot> source)
        {
            return source?
                .Select(v => new ModifyTopoService.SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList() ?? new List<ModifyTopoService.SculptVertexSnapshot>();
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
