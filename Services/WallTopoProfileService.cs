using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    internal sealed class AcceptAllFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor) =>
            FailureProcessingResult.Continue;
    }

    /// <summary>
    /// Builds vertical profile walls that follow a Toposolid (bottom on topo, top offset by height).
    /// </summary>
    internal static class WallTopoProfileService
    {
        private static readonly HashSet<BuiltInParameter> SkipRestoreBuiltIns = new HashSet<BuiltInParameter>
        {
            BuiltInParameter.WALL_BASE_OFFSET,
            BuiltInParameter.WALL_USER_HEIGHT_PARAM,
            BuiltInParameter.WALL_HEIGHT_TYPE,
            BuiltInParameter.WALL_TOP_OFFSET,
            BuiltInParameter.WALL_TOP_EXTENSION_DIST_PARAM,
            BuiltInParameter.WALL_BOTTOM_EXTENSION_DIST_PARAM,
            BuiltInParameter.WALL_CROSS_SECTION
        };

        internal sealed class ArchivedWallParameter
        {
            public string Name;
            public BuiltInParameter? BuiltIn;
            public StorageType StorageType;
            public double DoubleValue;
            public int IntValue;
            public string StringValue;
            public ElementId ElementIdValue;
        }

        public static List<ArchivedWallParameter> ArchiveWallParameters(Wall wall)
        {
            var list = new List<ArchivedWallParameter>();
            if (wall == null)
                return list;

            foreach (Parameter p in wall.Parameters)
            {
                if (p == null || p.IsReadOnly || p.Definition == null || !p.HasValue)
                    continue;

                if (p.Definition.Name.StartsWith("CURVE_ELEM", StringComparison.Ordinal))
                    continue;

                var entry = new ArchivedWallParameter
                {
                    Name = p.Definition.Name,
                    StorageType = p.StorageType
                };

                try { entry.BuiltIn = (BuiltInParameter)p.Id.Value; } catch { }

                switch (p.StorageType)
                {
                    case StorageType.Double:
                        entry.DoubleValue = p.AsDouble();
                        break;
                    case StorageType.Integer:
                        entry.IntValue = p.AsInteger();
                        break;
                    case StorageType.String:
                        entry.StringValue = p.AsString();
                        break;
                    case StorageType.ElementId:
                        entry.ElementIdValue = p.AsElementId();
                        break;
                    default:
                        continue;
                }

                list.Add(entry);
            }

            return list;
        }

        public static void RestoreWallParameters(Wall target, IEnumerable<ArchivedWallParameter> archive)
        {
            if (target == null || archive == null)
                return;

            foreach (ArchivedWallParameter entry in archive)
            {
                try
                {
                    if (entry.BuiltIn.HasValue && SkipRestoreBuiltIns.Contains(entry.BuiltIn.Value))
                        continue;

                    Parameter dst = entry.BuiltIn.HasValue
                        ? target.get_Parameter(entry.BuiltIn.Value)
                        : target.LookupParameter(entry.Name);
                    if (dst == null || dst.IsReadOnly)
                        continue;

                    switch (entry.StorageType)
                    {
                        case StorageType.Double:
                            dst.Set(entry.DoubleValue);
                            break;
                        case StorageType.Integer:
                            dst.Set(entry.IntValue);
                            break;
                        case StorageType.String:
                            dst.Set(entry.StringValue);
                            break;
                        case StorageType.ElementId:
                            dst.Set(entry.ElementIdValue);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not restore wall parameter {Name}: {Error}", entry.Name, ex.Message);
                }
            }
        }

        /// <summary>
        /// Bottom = topo samples; top = bottom + constant vertical wall height.
        /// </summary>
        public static IList<Curve> BuildTopoFollowProfile(
            IReadOnlyList<XYZ> planPoints,
            IReadOnlyList<double> bottomModelZ,
            double wallHeight)
        {
            if (planPoints == null || bottomModelZ == null || planPoints.Count < 2 ||
                planPoints.Count != bottomModelZ.Count)
                return Array.Empty<Curve>();

            var profile = new List<Curve>();
            int n = planPoints.Count;

            for (int i = 0; i < n - 1; i++)
            {
                XYZ a = planPoints[i];
                XYZ b = planPoints[i + 1];
                profile.Add(Line.CreateBound(
                    new XYZ(a.X, a.Y, bottomModelZ[i]),
                    new XYZ(b.X, b.Y, bottomModelZ[i + 1])));
            }

            XYZ last = planPoints[n - 1];
            XYZ first = planPoints[0];
            double topLast = bottomModelZ[n - 1] + wallHeight;
            double topFirst = bottomModelZ[0] + wallHeight;

            profile.Add(Line.CreateBound(
                new XYZ(last.X, last.Y, bottomModelZ[n - 1]),
                new XYZ(last.X, last.Y, topLast)));

            for (int i = n - 1; i > 0; i--)
            {
                XYZ a = planPoints[i];
                XYZ b = planPoints[i - 1];
                profile.Add(Line.CreateBound(
                    new XYZ(a.X, a.Y, bottomModelZ[i] + wallHeight),
                    new XYZ(b.X, b.Y, bottomModelZ[i - 1] + wallHeight)));
            }

            profile.Add(Line.CreateBound(
                new XYZ(first.X, first.Y, topFirst),
                new XYZ(first.X, first.Y, bottomModelZ[0])));

            return profile;
        }

        public static XYZ ComputeProfileWallNormal(XYZ pathStart, XYZ pathEnd, bool flipped)
        {
            XYZ dir = new XYZ(pathEnd.X - pathStart.X, pathEnd.Y - pathStart.Y, 0);
            if (dir.GetLength() < 1e-9)
                dir = XYZ.BasisX;
            else
                dir = dir.Normalize();

            XYZ normal = dir.CrossProduct(XYZ.BasisZ).Normalize();
            if (normal.GetLength() < 1e-9)
                normal = XYZ.BasisY;

            return flipped ? normal.Negate() : normal;
        }

        public static Wall TryCreateProfileWall(
            Document doc,
            IList<Curve> profile,
            ElementId wallTypeId,
            ElementId levelId,
            XYZ normal,
            bool structural)
        {
            if (doc == null || profile == null || profile.Count < 3)
                return null;

            try
            {
                return Wall.Create(doc, profile, wallTypeId, levelId, structural, normal);
            }
            catch (Exception ex)
            {
                Log.Debug("Wall.Create(profile) failed: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Replace elevation profile on an existing straight wall when supported.
        /// </summary>
        public static bool TryEditWallElevationProfile(Wall wall, IList<Curve> profile)
        {
            if (wall == null || profile == null || profile.Count < 3)
                return false;

            Document doc = wall.Document;
            try
            {
                if (!wall.IsValidObject)
                    return false;

                Sketch sketch = wall.SketchId != ElementId.InvalidElementId
                    ? doc.GetElement(wall.SketchId) as Sketch
                    : null;

                if (sketch == null)
                {
                    sketch = wall.CreateProfileSketch();
                    doc.Regenerate();
                }

                if (sketch == null)
                    return false;

                var scope = new SketchEditScope(doc, "Wall follow topo profile");
                scope.Start(sketch.Id);

                using (Transaction tx = new Transaction(doc, "Update wall profile"))
                {
                    tx.Start();
                    ClearSketchCurves(doc, sketch);
                    SketchPlane plane = sketch.SketchPlane;
                    if (plane == null)
                    {
                        tx.RollBack();
                        scope.Cancel();
                        return false;
                    }

                    foreach (Curve curve in profile)
                    {
                        if (curve == null) continue;
                        doc.Create.NewModelCurve(curve, plane);
                    }

                    tx.Commit();
                }

                scope.Commit(new AcceptAllFailuresPreprocessor());
                doc.Regenerate();
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("TryEditWallElevationProfile failed for wall {Id}: {Error}",
                    wall.Id.Value, ex.Message);
                return false;
            }
        }

        private static void ClearSketchCurves(Document doc, Sketch sketch)
        {
            if (doc == null || sketch == null)
                return;

            var toDelete = new List<ElementId>();
            try
            {
                foreach (ElementId id in sketch.GetAllElements())
                    toDelete.Add(id);
            }
            catch
            {
                foreach (ElementId id in new FilteredElementCollector(doc, sketch.Id).ToElementIds())
                    toDelete.Add(id);
            }

            if (toDelete.Count > 0)
                doc.Delete(toDelete);
        }

        public static bool IsStraightPath(Curve curve, out Line line)
        {
            line = curve as Line;
            return line != null;
        }

        public static IList<Curve> BuildSegmentProfile(
            XYZ p0, XYZ p1, double bottomZ0, double bottomZ1, double wallHeight)
        {
            double topZ0 = bottomZ0 + wallHeight;
            double topZ1 = bottomZ1 + wallHeight;

            XYZ bottomStart = new XYZ(p0.X, p0.Y, bottomZ0);
            XYZ bottomEnd = new XYZ(p1.X, p1.Y, bottomZ1);
            XYZ topEnd = new XYZ(p1.X, p1.Y, topZ1);
            XYZ topStart = new XYZ(p0.X, p0.Y, topZ0);

            return new List<Curve>
            {
                Line.CreateBound(bottomStart, bottomEnd),
                Line.CreateBound(bottomEnd, topEnd),
                Line.CreateBound(topEnd, topStart),
                Line.CreateBound(topStart, bottomStart)
            };
        }
    }
#endif
}
