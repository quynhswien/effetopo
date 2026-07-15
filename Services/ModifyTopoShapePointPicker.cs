using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Interactive point pick with hover preview after the dialog closes.</summary>
    internal sealed class ModifyTopoShapePointPicker
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VkLButton = 0x01;
        private const int VkEscape = 0x1B;

        private readonly UIApplication _uiApp;
        private readonly UIDocument _uidoc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoOptions _options;
        private readonly List<ModifyTopoService.SculptVertexSnapshot> _vertices;
        private readonly ModifyTopoGeometrySurfaceCache _geometry;
        private readonly ModifyTopoPreviewGraphics _graphics;

        private XYZ _currentCenter;
        private bool _finished;
        private bool _cancelled;
        private bool _wasMouseDown;
        private string _lastPreviewKey = string.Empty;

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
            _geometry = new ModifyTopoGeometrySurfaceCache(toposolid);
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

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_finished) return;
            e.SetRaiseWithoutDelay();

            if (IsKeyDown(VkEscape))
            {
                _cancelled = true;
                _finished = true;
                return;
            }

            bool mouseDown = IsKeyDown(VkLButton);
            if (ModifyTopoViewPickHelper.TryGetHitOnToposolid(
                    _uidoc, _toposolid, _geometry, IntPtr.Zero, out XYZ center, out ElementId viewId))
            {
                _currentCenter = center;
                if (_options.ShowPreview)
                {
                    string key = $"{center.X:F2}:{center.Y:F2}:{_options.ShapeRadiusFeet:F2}:{_options.ShapePointDensity}";
                    if (key != _lastPreviewKey)
                    {
                        _lastPreviewKey = key;
                        View view = _uidoc.Document.GetElement(viewId) as View;
                        var preview = _geometry.BuildStampPoints(center, _options, previewWithGain: true);
                        _graphics.UpdateMarkers(view, preview);
                        try { _uidoc.RefreshActiveView(); } catch { }
                    }
                }
            }
            else
            {
                _lastPreviewKey = string.Empty;
                _graphics.Clear();
            }

            if (mouseDown && !_wasMouseDown && _currentCenter != null)
                _finished = true;

            _wasMouseDown = mouseDown;
        }

        private static bool IsKeyDown(int virtualKey) =>
            (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
#endif
}
