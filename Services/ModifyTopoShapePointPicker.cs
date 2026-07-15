using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Interactive point pick with hover preview after the dialog closes.</summary>
    internal sealed class ModifyTopoShapePointPicker
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int VkLButton = 0x01;
        private const int VkEscape = 0x1B;

        private readonly UIApplication _uiApp;
        private readonly UIDocument _uidoc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoOptions _options;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _vertices;
        private readonly ModifyTopoPreviewGraphics _graphics;

        private XYZ _currentCenter;
        private bool _finished;
        private bool _cancelled;
        private bool _wasMouseDown;

        public ModifyTopoShapePointPicker(
            UIApplication uiApp,
            Toposolid toposolid,
            ModifyTopoOptions options)
        {
            _uiApp = uiApp;
            _uidoc = uiApp.ActiveUIDocument;
            _toposolid = toposolid;
            _options = options;
            _vertices = ModifyTopoService.Instance.GetVertexSnapshots(toposolid);
            _graphics = new ModifyTopoPreviewGraphics(_uidoc.Document);
        }

        public XYZ Pick()
        {
            _uiApp.Idling += OnIdling;

            try
            {
                var frame = new DispatcherFrame();
                while (!_finished)
                    Dispatcher.PushFrame(frame);

                return _cancelled ? null : _currentCenter;
            }
            finally
            {
                _uiApp.Idling -= OnIdling;
                _graphics.Clear();
            }
        }

        private void OnIdling(object sender, EventArgs e)
        {
            if (_finished) return;

            if (IsKeyDown(VkEscape))
            {
                _cancelled = true;
                _finished = true;
                return;
            }

            bool mouseDown = IsKeyDown(VkLButton);
            if (TryGetCursorOnToposolid(out XYZ center))
            {
                _currentCenter = center;
                if (_options.ShowPreview)
                {
                    var preview = ModifyTopoService.ComputeShapeByPointAddPreviewPoints(
                        center, _options, _vertices);
                    _graphics.UpdateMarkers(preview);
                }
            }

            if (mouseDown && !_wasMouseDown && _currentCenter != null)
                _finished = true;

            _wasMouseDown = mouseDown;
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

                double? z = ModifyTopoService.InterpolateSurfaceZ(
                    _vertices, x, y, Math.Max(_options.ShapeRadiusFeet, 5));
                if (!z.HasValue) return false;

                surfacePoint = new XYZ(x, y, z.Value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsKeyDown(int virtualKey) =>
            (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
#endif
}
