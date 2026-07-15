using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Draws transient square markers in the model for Shape-by-Point preview.</summary>
    internal sealed class ModifyTopoPreviewGraphics
    {
        private const string PreviewNamePrefix = "EFFETOPO_PREVIEW_";
        private const double MarkerHalfSizeFeet = 1.25;

        private readonly Document _doc;
        private readonly List<ElementId> _markerIds = new List<ElementId>();
        private ElementId _lastViewId = ElementId.InvalidElementId;
        private string _lastPointHash = string.Empty;

        public ModifyTopoPreviewGraphics(Document doc)
        {
            _doc = doc;
        }

        public void UpdateMarkers(View view, IList<XYZ> points)
        {
            if (_doc == null) return;

            string hash = BuildPointHash(points, view?.Id);
            if (hash == _lastPointHash)
                return;
            _lastPointHash = hash;

            using (Transaction tx = new Transaction(_doc, "Modify Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearMarkersInternal();
                    if (points != null && view != null)
                    {
                        int limit = Math.Min(points.Count, 500);
                        for (int i = 0; i < limit; i++)
                            CreateMarker(points[i], i);

                        ApplyPreviewOverrides(view);
                        _lastViewId = view.Id;
                    }

                    _doc.Regenerate();
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    Log.Warning("Preview marker update failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded())
                        tx.RollBack();
                }
            }
        }

        public void Clear()
        {
            _lastPointHash = string.Empty;
            if (_doc == null || _markerIds.Count == 0) return;

            using (Transaction tx = new Transaction(_doc, "Clear Modify Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearMarkerOverrides();
                    ClearMarkersInternal();
                    _doc.Regenerate();
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    Log.Debug("Clear preview failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded())
                        tx.RollBack();
                }
            }
        }

        private void ApplyPreviewOverrides(View view)
        {
            if (view == null) return;

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(new Color(255, 30, 30));
            ogs.SetSurfaceForegroundPatternColor(new Color(255, 30, 30));
            ogs.SetSurfaceBackgroundPatternColor(new Color(255, 30, 30));
            ogs.SetCutLineColor(new Color(255, 30, 30));
            ogs.SetSurfaceTransparency(10);

            foreach (ElementId id in _markerIds)
            {
                try { view.SetElementOverrides(id, ogs); }
                catch { }
            }
        }

        private void ClearMarkerOverrides()
        {
            if (_lastViewId == null || _lastViewId == ElementId.InvalidElementId)
                return;

            View view = _doc.GetElement(_lastViewId) as View;
            if (view == null) return;

            var reset = new OverrideGraphicSettings();
            foreach (ElementId id in _markerIds)
            {
                try { view.SetElementOverrides(id, reset); }
                catch { }
            }
        }

        private void ClearMarkersInternal()
        {
            foreach (ElementId id in _markerIds.ToList())
            {
                try
                {
                    if (id != null && id != ElementId.InvalidElementId)
                        _doc.Delete(id);
                }
                catch { }
            }
            _markerIds.Clear();
        }

        private void CreateMarker(XYZ center, int index)
        {
            if (center == null) return;

            double h = MarkerHalfSizeFeet;
            double z = center.Z + 0.05;
            var profile = new List<Curve>
            {
                Line.CreateBound(new XYZ(center.X - h, center.Y - h, z), new XYZ(center.X + h, center.Y - h, z)),
                Line.CreateBound(new XYZ(center.X + h, center.Y - h, z), new XYZ(center.X + h, center.Y + h, z)),
                Line.CreateBound(new XYZ(center.X + h, center.Y + h, z), new XYZ(center.X - h, center.Y + h, z)),
                Line.CreateBound(new XYZ(center.X - h, center.Y + h, z), new XYZ(center.X - h, center.Y - h, z))
            };

            var loop = CurveLoop.Create(profile);
            var loops = new List<CurveLoop> { loop };
            var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
            if (ds == null) return;

            ds.SetShape(new GeometryObject[] { GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 0.35) });
            ds.Name = $"{PreviewNamePrefix}{index}";
            _markerIds.Add(ds.Id);
        }

        private static string BuildPointHash(IList<XYZ> points, ElementId viewId)
        {
            if (points == null || points.Count == 0) return string.Empty;
            int n = Math.Min(points.Count, 12);
            var parts = new string[n];
            for (int i = 0; i < n; i++)
            {
                XYZ p = points[i];
                parts[i] = $"{p.X:F2},{p.Y:F2},{p.Z:F2}";
            }
            return $"{viewId}:{points.Count}:{string.Join("|", parts)}";
        }
    }
#endif
}
