using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Maps Windows cursor position to a point on a Toposolid face.</summary>
    internal static class ModifyTopoViewPickHelper
    {
        private const uint GaRoot = 2;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public static bool IsCursorOverExcludedWindow(IntPtr excludeWindowHwnd)
        {
            if (excludeWindowHwnd == IntPtr.Zero)
                return false;
            if (!GetCursorPos(out POINT screen))
                return false;
            return IsCursorOverWindow(screen, excludeWindowHwnd);
        }

        private const int ViewRectMarginPx = 48;

        public static bool TryGetHitOnToposolid(
            UIDocument uidoc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IntPtr excludeWindowHwnd,
            out XYZ hitPoint,
            out ElementId viewId)
        {
            hitPoint = null;
            viewId = ElementId.InvalidElementId;

            if (uidoc?.Document == null || toposolid == null)
                return false;

            if (!GetCursorPos(out POINT screen))
                return false;

            if (IsCursorOverWindow(screen, excludeWindowHwnd))
                return false;

            UIView uiView = FindUiViewUnderCursor(uidoc, screen);
            if (uiView == null)
                return false;

            Document doc = uidoc.Document;
            View view = doc.GetElement(uiView.ViewId) as View;
            if (view == null)
                return false;

            viewId = view.Id;

            if (!TryComputeModelPointOnViewPlane(view, uiView, screen, out XYZ planePoint))
                return false;

            XYZ viewDir = view.ViewDirection;
            var vertices = ModifyTopoService.Instance.GetVertexSnapshots(toposolid);
            double refZ = GetReferenceElevation(toposolid, vertices);

            double pickX = planePoint.X;
            double pickY = planePoint.Y;

            if (view is View3D view3d)
            {
                try
                {
                    XYZ origin = planePoint + 1000 * viewDir;
                    var intersector = new ReferenceIntersector(
                        toposolid.Id, FindReferenceTarget.Face, view3d);
                    ReferenceWithContext hit = intersector.FindNearest(origin, -viewDir);
                    XYZ global = hit?.GetReference()?.GlobalPoint;
                    if (global != null)
                    {
                        pickX = global.X;
                        pickY = global.Y;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("ReferenceIntersector pick failed: {Error}", ex.Message);
                }
            }

            var survey = new RevitAlongSurfaceSampler.SurveyCoordinateHelper(doc);
            if (RevitAlongSurfaceSampler.TrySampleAtXY(
                    doc, toposolid, geometry, vertices, view, pickX, pickY, survey,
                    out RevitAlongSurfaceSampler.AlongSurfaceSample sample))
            {
                hitPoint = sample.ModelPoint;
                return true;
            }

            hitPoint = new XYZ(pickX, pickY, refZ);
            return true;
        }

        public static bool TryGetHitOnToposolid(
            UIDocument uidoc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IntPtr excludeWindowHwnd,
            out RevitAlongSurfaceSampler.AlongSurfaceSample sample,
            out ElementId viewId)
        {
            sample = null;
            viewId = ElementId.InvalidElementId;

            if (!TryGetHitOnToposolid(
                    uidoc, toposolid, geometry, excludeWindowHwnd, out XYZ hitPoint, out viewId))
                return false;

            if (hitPoint == null)
                return false;

            var survey = new RevitAlongSurfaceSampler.SurveyCoordinateHelper(uidoc.Document);
            sample = new RevitAlongSurfaceSampler.AlongSurfaceSample
            {
                ModelPoint = hitPoint,
                TopFaceModelZ = hitPoint.Z,
                SurveyElevationFt = survey.ModelZToSurveyElevation(hitPoint.X, hitPoint.Y, hitPoint.Z)
            };
            return true;
        }

        private static bool TryComputeModelPointOnViewPlane(
            View view, UIView uiView, POINT screen, out XYZ modelPoint)
        {
            modelPoint = null;

            Autodesk.Revit.DB.Rectangle rect = uiView.GetWindowRectangle();
            double viewWidth = rect.Right - rect.Left;
            double viewHeight = rect.Bottom - rect.Top;
            if (viewWidth <= 1 || viewHeight <= 1)
                return false;

            double dx = (screen.X - rect.Left) / viewWidth;
            double dy = (screen.Y - rect.Bottom) / (rect.Top - rect.Bottom);
            dx = Math.Max(0, Math.Min(1, dx));
            dy = Math.Max(0, Math.Min(1, dy));

            IList<XYZ> corners = uiView.GetZoomCorners();
            if (corners == null || corners.Count < 2)
                return false;

            XYZ a = corners[0];
            XYZ b = corners[1];
            XYZ v = b - a;

            XYZ right = view.RightDirection;
            XYZ up = view.UpDirection;
            double projRight = v.DotProduct(right);
            double projUp = v.DotProduct(up);

            if (Math.Abs(projRight) > 1e-6 && Math.Abs(projUp) > 1e-6)
            {
                modelPoint = a + dx * projRight * right + dy * projUp * up;
                return true;
            }

            double minX = Math.Min(a.X, b.X);
            double maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y);
            double maxY = Math.Max(a.Y, b.Y);
            double minZ = Math.Min(a.Z, b.Z);
            double maxZ = Math.Max(a.Z, b.Z);

            modelPoint = new XYZ(
                minX + dx * (maxX - minX),
                minY + dy * (maxY - minY),
                minZ + dy * (maxZ - minZ));
            return true;
        }

        private static bool TryRayIntersectHorizontalPlane(
            XYZ planePoint, XYZ viewDir, double elevation, out XYZ hit)
        {
            hit = null;
            XYZ origin = planePoint + 1000 * viewDir;
            XYZ direction = -viewDir;

            if (Math.Abs(direction.Z) < 1e-9)
                return false;

            double t = (elevation - origin.Z) / direction.Z;
            if (t < 0)
                return false;

            hit = origin + t * direction;
            return true;
        }

        private static double GetReferenceElevation(Toposolid toposolid, IList<ModifyTopoService.SculptVertexSnapshot> vertices)
        {
            if (vertices != null && vertices.Count > 0)
                return vertices.Average(v => v.Z);

            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb != null)
                    return (bb.Min.Z + bb.Max.Z) * 0.5;
            }
            catch { }

            return 0;
        }

        private static double GetTopoHorizontalSize(Toposolid toposolid)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb == null) return 20;
                return Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            }
            catch
            {
                return 20;
            }
        }

        private static UIView FindUiViewUnderCursor(UIDocument uidoc, POINT screen)
        {
            UIView best = null;
            int bestArea = int.MaxValue;

            foreach (UIView uiView in uidoc.GetOpenUIViews())
            {
                Autodesk.Revit.DB.Rectangle rect = uiView.GetWindowRectangle();
                if (!IsInsideExpandedRect(screen, rect, ViewRectMarginPx))
                    continue;

                int area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = uiView;
                }
            }

            if (best != null)
                return best;

            return uidoc.GetOpenUIViews()
                .FirstOrDefault(v => v.ViewId == uidoc.ActiveView?.Id);
        }

        private static bool IsInsideExpandedRect(POINT screen, Autodesk.Revit.DB.Rectangle rect, int margin)
        {
            return screen.X >= rect.Left - margin && screen.X <= rect.Right + margin &&
                   screen.Y >= rect.Top - margin && screen.Y <= rect.Bottom + margin;
        }

        private static bool IsCursorOverWindow(POINT screen, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            IntPtr atPoint = WindowFromPoint(screen);
            if (atPoint == IntPtr.Zero)
                return false;

            return atPoint == hwnd || GetAncestor(atPoint, GaRoot) == hwnd;
        }

        private static bool IsNearToposolid(Toposolid toposolid, XYZ point, double margin)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb == null) return true;

                return point.X >= bb.Min.X - margin && point.X <= bb.Max.X + margin &&
                       point.Y >= bb.Min.Y - margin && point.Y <= bb.Max.Y + margin;
            }
            catch
            {
                return true;
            }
        }
    }
#endif
}
