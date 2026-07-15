using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    internal sealed class ModifyTopoPreviewSession
    {
        public sealed class StampRecord
        {
            public XYZ Center { get; set; }
            public ModifyTopoOptions Options { get; set; }
        }

        private readonly ModifyTopoGeometrySurfaceCache _geometry;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _baseVertices;
        private readonly List<StampRecord> _committedStamps = new List<StampRecord>();

        public ModifyTopoPreviewSession(
            Toposolid toposolid,
            IList<ModifyTopoService.SculptVertexSnapshot> baseVertices)
        {
            _geometry = new ModifyTopoGeometrySurfaceCache(toposolid);
            _baseVertices = baseVertices?
                .Select(v => new ModifyTopoService.SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList() ?? new List<ModifyTopoService.SculptVertexSnapshot>();
        }

        public ModifyTopoGeometrySurfaceCache Geometry => _geometry;

        public IReadOnlyList<StampRecord> CommittedStamps => _committedStamps;

        public void RefreshBaseFromToposolid(Toposolid toposolid)
        {
            _baseVertices.Clear();
            foreach (var v in ModifyTopoService.Instance.GetVertexSnapshots(toposolid))
                _baseVertices.Add(new ModifyTopoService.SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z });
        }

        public void RecordStamp(XYZ center, ModifyTopoOptions options)
        {
            if (center == null || options == null) return;
            _committedStamps.Add(new StampRecord
            {
                Center = center,
                Options = CloneOptions(options)
            });
        }

        public List<XYZ> GetHoverStampPoints(XYZ hoverCenter, ModifyTopoOptions options)
        {
            if (hoverCenter == null || options == null)
                return new List<XYZ>();

            return _geometry.BuildStampPoints(hoverCenter, options, previewWithGain: true);
        }

        public XYZ GetHoverCenterOnSurface(XYZ hoverCenter, ModifyTopoOptions options)
        {
            if (hoverCenter == null || options == null)
                return hoverCenter;

            double? surfaceZ = _geometry.TryGetSurfaceZ(hoverCenter.X, hoverCenter.Y);
            if (!surfaceZ.HasValue)
                return hoverCenter;

            return new XYZ(hoverCenter.X, hoverCenter.Y, surfaceZ.Value + options.ShapeDeltaFeet);
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
#endif
}
