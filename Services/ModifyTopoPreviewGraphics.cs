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
        private const double MarkerHalfSizeFeet = 0.35;

        private readonly Document _doc;
        private readonly List<ElementId> _markerIds = new List<ElementId>();

        public ModifyTopoPreviewGraphics(Document doc)
        {
            _doc = doc;
        }

        public void UpdateMarkers(IList<XYZ> points)
        {
            if (_doc == null) return;

            using (Transaction tx = new Transaction(_doc, "Modify Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearMarkersInternal();
                    if (points != null)
                    {
                        int limit = Math.Min(points.Count, 400);
                        for (int i = 0; i < limit; i++)
                            CreateMarker(points[i], i);
                    }
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded())
                        tx.RollBack();
                }
            }
        }

        public void Clear()
        {
            if (_doc == null || _markerIds.Count == 0) return;
            using (Transaction tx = new Transaction(_doc, "Clear Modify Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearMarkersInternal();
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded())
                        tx.RollBack();
                }
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
            var profile = new List<Curve>
            {
                Line.CreateBound(new XYZ(center.X - h, center.Y - h, center.Z), new XYZ(center.X + h, center.Y - h, center.Z)),
                Line.CreateBound(new XYZ(center.X + h, center.Y - h, center.Z), new XYZ(center.X + h, center.Y + h, center.Z)),
                Line.CreateBound(new XYZ(center.X + h, center.Y + h, center.Z), new XYZ(center.X - h, center.Y + h, center.Z)),
                Line.CreateBound(new XYZ(center.X - h, center.Y + h, center.Z), new XYZ(center.X - h, center.Y - h, center.Z))
            };

            var loop = CurveLoop.Create(profile);
            var loops = new List<CurveLoop> { loop };
            var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
            if (ds == null) return;

            ds.SetShape(new GeometryObject[] { GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, 0.05) });
            ds.Name = $"{PreviewNamePrefix}{index}";
            _markerIds.Add(ds.Id);
        }
    }
#endif
}
