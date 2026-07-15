using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Preview surface = DirectShape copy of Toposolid display geometry + stamp overlay.
    /// Revit has no native "preview surface" API; DirectShape is the standard approach.
    /// </summary>
    internal sealed class ModifyTopoPreviewSurface
    {
        private const string BaseName = "EFFETOPO_PREVIEW_BASE";
        private const string HoverName = "EFFETOPO_PREVIEW_HOVER";
        private const string DraftShellName = "EFFETOPO_PREVIEW_DRAFT_SHELL";
        private const string DeltaVolumeName = "EFFETOPO_PREVIEW_DELTA";
        private const string LabelName = "EFFETOPO_PREVIEW_LABEL";

        private readonly Document _doc;
        private ElementId _baseId = ElementId.InvalidElementId;
        private ElementId _hoverId = ElementId.InvalidElementId;
        private ElementId _draftShellId = ElementId.InvalidElementId;
        private ElementId _deltaVolumeId = ElementId.InvalidElementId;
        private ElementId _labelId = ElementId.InvalidElementId;
        private ElementId _lastViewId = ElementId.InvalidElementId;
        private ElementId _dimmedTopoId = ElementId.InvalidElementId;
        private string _lastPreviewKey = string.Empty;
        private string _lastDraftKey = string.Empty;

        public ModifyTopoPreviewSurface(Document doc)
        {
            _doc = doc;
        }

        /// <summary>Create semi-transparent copy of topo display mesh (model coordinates).</summary>
        public void EnsureBaseOverlay(ModifyTopoGeometrySurfaceCache geometry, View view)
        {
            if (_doc == null || geometry == null || view == null) return;
            if (_baseId != ElementId.InvalidElementId && _doc.GetElement(_baseId) != null)
            {
                ApplySurfaceStyle(view, _baseId, new Color(80, 180, 255), 55);
                return;
            }

            IReadOnlyList<Solid> solids = geometry.Solids;
            if (solids == null || solids.Count == 0)
            {
                Log.Warning("Preview base overlay: no solid geometry extracted from Toposolid");
                return;
            }

            using (Transaction tx = new Transaction(_doc, "Create Topo Preview Surface"))
            {
                tx.Start();
                try
                {
                    ClearElement(ref _baseId);
                    var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(solids.Cast<GeometryObject>().ToArray());
                    ds.Name = BaseName;
                    _baseId = ds.Id;
                    ApplySurfaceStyle(view, _baseId, new Color(80, 180, 255), 55);
                    _lastViewId = view.Id;
                    _doc.Regenerate();
                    tx.Commit();
                    Log.Information("Preview base surface created ({SolidCount} solids)", solids.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning("Create preview surface failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        /// <summary>
        /// Draft preview: yellow deformed surface shell + semi-transparent delta volume vs existing mesh.
        /// </summary>
        public void UpdateDraftPreview(
            View view,
            ElementId toposolidId,
            ModifyTopoGeometrySurfaceCache geometry,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IEnumerable<ModifyTopoDraftSession.StampRecord> stamps,
            int stampCount)
        {
            if (_doc == null || view == null || geometry == null)
                return;

            ModifyTopoDraftSurfaceBuilder.GetBoundsFromStamps(stamps, out double minX, out double minY, out double maxX, out double maxY);
            double zChecksum = 0;
            if (workingVertices != null)
            {
                foreach (var v in workingVertices)
                    zChecksum += v.Z;
            }

            string key = string.Format(CultureInfo.InvariantCulture,
                "draft:{0}:{1}:{2:F1}",
                stampCount, workingVertices?.Count ?? 0, zChecksum);
            if (key == _lastDraftKey)
                return;
            _lastDraftKey = key;

            ModifyTopoDraftSurfaceBuilder.DraftPreviewGeometry built =
                ModifyTopoDraftSurfaceBuilder.Build(
                    geometry, baseVertices, workingVertices, stamps, minX, minY, maxX, maxY);

            var allPreviewSolids = new List<GeometryObject>();
            allPreviewSolids.AddRange(built.DeltaVolumes);
            allPreviewSolids.AddRange(built.DraftSurface);

            using (Transaction tx = new Transaction(_doc, "Update Draft Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearElement(ref _hoverId);
                    ClearElement(ref _draftShellId);
                    ClearElement(ref _deltaVolumeId);
                    ClearAllPreviewLabels();

                    if (allPreviewSolids.Count > 0)
                    {
                        var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        ds.SetShape(allPreviewSolids);
                        ds.Name = DraftShellName;
                        _draftShellId = ds.Id;
                        ApplySurfaceStyle(view, _draftShellId, new Color(255, 200, 0), 5);
                    }
                    else
                    {
                        Log.Warning(
                            "Draft preview: no visible solids ({TriCount} tris). Check Show Preview and stamp size.",
                            built.ChangedTriangleCount);
                    }

                    if (stampCount > 0 && stamps != null)
                    {
                        ModifyTopoDraftSession.StampRecord last = null;
                        foreach (ModifyTopoDraftSession.StampRecord s in stamps)
                            last = s;
                        if (last?.Center != null)
                        {
                            CreateDraftLabel(
                                view, last.Center,
                                $"{stampCount} stamp — {allPreviewSolids.Count} solids");
                        }
                    }

                    ApplyTopoDimming(view, toposolidId, dim: true);
                    _lastViewId = view.Id;
                    _doc.Regenerate();
                    tx.Commit();

                    Log.Information(
                        "Draft preview: {TriCount} tris, {DraftCount} draft solids, {DeltaCount} delta solids",
                        built.ChangedTriangleCount, built.DraftSurface.Count, built.DeltaVolumes.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning("Update draft preview failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        private void ApplyTopoDimming(View view, ElementId toposolidId, bool dim)
        {
            if (view == null || toposolidId == null || toposolidId == ElementId.InvalidElementId)
                return;

            try
            {
                if (dim)
                {
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceTransparency(72);
                    ogs.SetProjectionLineColor(new Color(140, 140, 140));
                    view.SetElementOverrides(toposolidId, ogs);
                    _dimmedTopoId = toposolidId;
                }
                else if (_dimmedTopoId != ElementId.InvalidElementId)
                {
                    view.SetElementOverrides(_dimmedTopoId, new OverrideGraphicSettings());
                    _dimmedTopoId = ElementId.InvalidElementId;
                }
            }
            catch { }
        }

        public void UpdatePreview(
            View view,
            XYZ hoverCenter,
            ModifyTopoOptions options,
            IList<XYZ> stampPoints)
        {
            if (_doc == null || view == null) return;

            int pointCount = stampPoints?.Count ?? 0;
            string key = BuildPreviewKey(hoverCenter, options, pointCount);
            if (key == _lastPreviewKey) return;
            _lastPreviewKey = key;

            using (Transaction tx = new Transaction(_doc, "Update Topo Preview"))
            {
                tx.Start();
                try
                {
                    ClearElement(ref _hoverId);
                    ClearElement(ref _labelId);
                    var shapes = new List<GeometryObject>();

                    if (stampPoints != null)
                    {
                        foreach (XYZ pt in stampPoints)
                        {
                            Solid marker = CreatePointMarker(pt, 0.35);
                            if (marker != null)
                                shapes.Add(marker);
                        }
                    }

                    if (hoverCenter != null && options != null && stampPoints != null && stampPoints.Count > 0)
                    {
                        double ringZ = stampPoints.Average(p => p.Z);
                        Solid ring = CreateRadiusRing(hoverCenter.X, hoverCenter.Y, options.ShapeRadiusFeet, ringZ);
                        if (ring != null)
                            shapes.Add(ring);
                    }

                    if (shapes.Count > 0)
                    {
                        var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        ds.SetShape(shapes.ToArray());
                        ds.Name = HoverName;
                        _hoverId = ds.Id;
                        ApplySurfaceStyle(view, _hoverId, new Color(255, 200, 0), 0);
                        _lastViewId = view.Id;
                    }

                    if (hoverCenter != null && pointCount > 0)
                        CreatePointCountLabel(view, hoverCenter, pointCount);

                    _doc.Regenerate();
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    Log.Warning("Update preview failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        public void Clear()
        {
            _lastPreviewKey = string.Empty;
            _lastDraftKey = string.Empty;
            using (Transaction tx = new Transaction(_doc, "Clear Topo Preview"))
            {
                tx.Start();
                try
                {
                    View view = _lastViewId != ElementId.InvalidElementId
                        ? _doc.GetElement(_lastViewId) as View
                        : null;
                    ApplyTopoDimming(view, _dimmedTopoId, dim: false);
                    ClearOverrides();
                    ClearElement(ref _hoverId);
                    ClearAllPreviewLabels();
                    ClearElement(ref _baseId);
                    ClearElement(ref _draftShellId);
                    ClearElement(ref _deltaVolumeId);
                    _doc.Regenerate();
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        private void ClearAllPreviewLabels()
        {
            ClearElement(ref _labelId);
            try
            {
                var toDelete = new List<ElementId>();
                foreach (Element e in new FilteredElementCollector(_doc)
                    .OfClass(typeof(TextNote))
                    .WhereElementIsNotElementType())
                {
                    if (e is TextNote note && note.Text != null &&
                        (note.Text.Contains("stamp") || note.Text.Contains("solids")))
                        toDelete.Add(note.Id);
                }
                if (toDelete.Count > 0)
                    _doc.Delete(toDelete);
            }
            catch (Exception ex)
            {
                Log.Debug("Clear preview labels: {Error}", ex.Message);
            }
        }

        private void CreateDraftLabel(View view, XYZ center, string text)
        {
            try
            {
                ElementId textTypeId = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                if (textTypeId == ElementId.InvalidElementId)
                    return;

                var labelPos = new XYZ(center.X, center.Y, center.Z + 2.0);
                var opts = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Center
                };
                TextNote note = TextNote.Create(_doc, view.Id, labelPos, text, opts);
                if (note != null)
                {
                    note.Name = LabelName;
                    _labelId = note.Id;
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(new Color(255, 220, 0));
                    view.SetElementOverrides(_labelId, ogs);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Draft label failed: {Error}", ex.Message);
            }
        }

        private void CreatePointCountLabel(View view, XYZ center, int pointCount)
        {
            try
            {
                ElementId textTypeId = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                if (textTypeId == ElementId.InvalidElementId)
                    return;

                var labelPos = new XYZ(center.X, center.Y, center.Z + 1.5);
                var opts = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Center
                };
                TextNote note = TextNote.Create(
                    _doc, view.Id, labelPos,
                    $"{pointCount} pts",
                    opts);
                if (note != null)
                {
                    note.Name = LabelName;
                    _labelId = note.Id;
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(new Color(255, 220, 0));
                    view.SetElementOverrides(_labelId, ogs);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Point count label failed: {Error}", ex.Message);
            }
        }

        private static Solid CreateRadiusRing(double cx, double cy, double radius, double elevation)
        {
            try
            {
                radius = Math.Max(radius, 0.5);
                double z = elevation + 0.15;
                var loop = CurveLoop.Create(CreateCircle(cx, cy, radius, z));
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop },
                    XYZ.BasisZ,
                    0.2);
            }
            catch
            {
                return null;
            }
        }

        private static IList<Curve> CreateCircle(double cx, double cy, double radius, double z)
        {
            const int segments = 32;
            var curves = new List<Curve>();
            XYZ prev = null;
            for (int i = 0; i <= segments; i++)
            {
                double a = 2.0 * Math.PI * i / segments;
                var pt = new XYZ(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a), z);
                if (prev != null)
                    curves.Add(Line.CreateBound(prev, pt));
                prev = pt;
            }
            return curves;
        }

        private static Solid CreatePointMarker(XYZ point, double halfSize)
        {
            if (point == null) return null;
            try
            {
                double z = point.Z + 0.1;
                var profile = new List<Curve>
                {
                    Line.CreateBound(new XYZ(point.X - halfSize, point.Y - halfSize, z),
                        new XYZ(point.X + halfSize, point.Y - halfSize, z)),
                    Line.CreateBound(new XYZ(point.X + halfSize, point.Y - halfSize, z),
                        new XYZ(point.X + halfSize, point.Y + halfSize, z)),
                    Line.CreateBound(new XYZ(point.X + halfSize, point.Y + halfSize, z),
                        new XYZ(point.X - halfSize, point.Y + halfSize, z)),
                    Line.CreateBound(new XYZ(point.X - halfSize, point.Y + halfSize, z),
                        new XYZ(point.X - halfSize, point.Y - halfSize, z))
                };
                var loop = CurveLoop.Create(profile);
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, 0.35);
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureCategoryVisible(View view, BuiltInCategory category)
        {
            if (view == null) return;
            try
            {
                if (view.GetCategoryHidden(new ElementId(category)))
                    view.SetCategoryHidden(new ElementId(category), false);
            }
            catch { }
        }

        private void ApplySurfaceStyle(View view, ElementId id, Color color, int transparency)
        {
            if (view == null || id == null || id == ElementId.InvalidElementId) return;
            EnsureCategoryVisible(view, BuiltInCategory.OST_GenericModel);
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetSurfaceBackgroundPatternColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetSurfaceTransparency(transparency);
            ogs.SetSurfaceForegroundPatternVisible(true);
            ogs.SetSurfaceBackgroundPatternVisible(true);
            try
            {
                ogs.SetSurfaceForegroundPatternId(GetSolidFillPatternId());
            }
            catch { }
            view.SetElementOverrides(id, ogs);
        }

        private ElementId GetSolidFillPatternId()
        {
            try
            {
                return new FilteredElementCollector(_doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)?.Id
                    ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private void ClearOverrides()
        {
            if (_lastViewId == null || _lastViewId == ElementId.InvalidElementId) return;
            View view = _doc.GetElement(_lastViewId) as View;
            if (view == null) return;

            var reset = new OverrideGraphicSettings();
            foreach (ElementId id in new[] { _baseId, _hoverId, _labelId, _draftShellId, _deltaVolumeId })
            {
                if (id != null && id != ElementId.InvalidElementId)
                {
                    try { view.SetElementOverrides(id, reset); } catch { }
                }
            }
        }

        private void ClearElement(ref ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            try
            {
                if (_doc.GetElement(id) != null)
                    _doc.Delete(id);
            }
            catch { }
            id = ElementId.InvalidElementId;
        }

        private static string BuildPreviewKey(XYZ center, ModifyTopoOptions options, int pointCount)
        {
            if (center == null || options == null) return $"none:{pointCount}";
            return string.Format(CultureInfo.InvariantCulture,
                "{0:F2},{1:F2}:{2:F2}:{3:F2}:{4}",
                center.X, center.Y, options.ShapeRadiusFeet, options.ShapeDeltaFeet, pointCount);
        }
    }
#endif
}
