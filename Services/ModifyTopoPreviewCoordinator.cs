using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Models;
using effetopo.Views;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Live preview of Shape-by-Point grid while the modeless Modify Topo dialog is open.
    /// </summary>
    internal sealed class ModifyTopoPreviewCoordinator : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private readonly UIApplication _uiApp;
        private readonly UIDocument _uidoc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoDialog _dialog;
        private readonly ModifyTopoPreviewGraphics _graphics;

        private List<ModifyTopoService.SculptVertexSnapshot> _vertices = new List<ModifyTopoService.SculptVertexSnapshot>();

        public ModifyTopoPreviewCoordinator(
            UIApplication uiApp, Toposolid toposolid, ModifyTopoDialog dialog)
        {
            _uiApp = uiApp;
            _uidoc = uiApp.ActiveUIDocument;
            _toposolid = toposolid;
            _dialog = dialog;
            _graphics = new ModifyTopoPreviewGraphics(_uidoc.Document);
        }

        public void Start()
        {
            _vertices = ModifyTopoService.Instance.GetVertexSnapshots(_toposolid);
            _uiApp.Idling += OnIdling;
            _dialog.LiveOptionsChanged += OnLiveOptionsChanged;
        }

        public void Dispose()
        {
            _uiApp.Idling -= OnIdling;
            if (_dialog != null)
                _dialog.LiveOptionsChanged -= OnLiveOptionsChanged;
            _graphics.Clear();
        }

        private void OnLiveOptionsChanged(object sender, EventArgs e) => RefreshPreview();

        private void OnIdling(object sender, EventArgs e)
        {
            if (_dialog == null || !_dialog.IsVisible)
                return;
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint ||
                !options.ShowPreview)
            {
                _graphics.Clear();
                return;
            }

            if (!TryGetCursorOnToposolid(out XYZ center))
            {
                _graphics.Clear();
                return;
            }

            var previewPoints = ModifyTopoService.ComputeShapeByPointAddPreviewPoints(
                center, options, _vertices);
            _graphics.UpdateMarkers(previewPoints);
        }

        private bool TryGetCursorOnToposolid(out XYZ surfacePoint)
        {
            surfacePoint = null;
            try
            {
                if (!GetCursorPos(out POINT screen)) return false;

                UIView uiView = _uidoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == _uidoc.ActiveView.Id);
                if (uiView == null) return false;

                Autodesk.Revit.DB.Rectangle rect = uiView.GetWindowRectangle();
                if (screen.X < rect.Left || screen.X > rect.Right ||
                    screen.Y < rect.Top || screen.Y > rect.Bottom)
                    return false;

                double relX = screen.X - rect.Left;
                double relY = screen.Y - rect.Top;
                double viewWidth = rect.Right - rect.Left;
                double viewHeight = rect.Bottom - rect.Top;
                if (viewWidth <= 1 || viewHeight <= 1) return false;

                IList<XYZ> corners = uiView.GetZoomCorners();
                if (corners == null || corners.Count < 2) return false;

                double u = relX / viewWidth;
                double v = relY / viewHeight;
                XYZ c0 = corners[0];
                XYZ c1 = corners[1];
                double x = c0.X + u * (c1.X - c0.X);
                double y = c0.Y + v * (c1.Y - c0.Y);

                double? z = ModifyTopoService.InterpolateSurfaceZ(_vertices, x, y, 30);
                if (!z.HasValue) return false;

                surfacePoint = new XYZ(x, y, z.Value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
#endif
}
