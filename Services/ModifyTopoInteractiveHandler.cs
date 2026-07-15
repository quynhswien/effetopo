using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    internal enum ModifyTopoInteractiveAction
    {
        None,
        PreviewTick,
        ClearPreview,
        ApplyShapeByPoint
    }

    /// <summary>All Revit API work for Shape-by-Point preview/apply runs here.</summary>
    internal sealed class ModifyTopoInteractiveHandler : IExternalEventHandler
    {
        private readonly object _lock = new object();

        private ModifyTopoInteractiveAction _action;
        private UIDocument _uidoc;
        private Document _document;
        private Toposolid _toposolid;
        private IntPtr _excludeHwnd;
        private ModifyTopoOptions _options;
        private XYZ _center;
        private ModifyTopoPreviewSurface _surface;
        private ModifyTopoPreviewSession _session;
        private bool _clickToApply;
        private Action<ModifyTopoResult> _applyCallback;
        private Action<string> _statusCallback;

        public ExternalEvent ExternalEvent { get; }

        public ModifyTopoInteractiveHandler()
        {
            ExternalEvent = ExternalEvent.Create(this);
        }

        public void QueuePreviewTick(
            UIDocument uidoc,
            Document doc,
            Toposolid toposolid,
            IntPtr excludeDialogHwnd,
            ModifyTopoOptions options,
            ModifyTopoPreviewSurface surface,
            ModifyTopoPreviewSession session,
            bool clickToApply,
            Action<string> statusCallback,
            Action<ModifyTopoResult> applyCallback)
        {
            lock (_lock)
            {
                _action = ModifyTopoInteractiveAction.PreviewTick;
                _uidoc = uidoc;
                _document = doc;
                _toposolid = toposolid;
                _excludeHwnd = excludeDialogHwnd;
                _options = options;
                _surface = surface;
                _session = session;
                _clickToApply = clickToApply;
                _statusCallback = statusCallback;
                _applyCallback = applyCallback;
            }
            ExternalEvent.Raise();
        }

        public void QueueClearPreview(ModifyTopoPreviewSurface surface, UIDocument uidoc)
        {
            lock (_lock)
            {
                _action = ModifyTopoInteractiveAction.ClearPreview;
                _surface = surface;
                _uidoc = uidoc;
            }
            ExternalEvent.Raise();
        }

        public void QueueApplyShapeByPoint(
            UIDocument uidoc,
            Document doc,
            Toposolid toposolid,
            XYZ center,
            ModifyTopoOptions options,
            Action<ModifyTopoResult> callback)
        {
            lock (_lock)
            {
                _action = ModifyTopoInteractiveAction.ApplyShapeByPoint;
                _uidoc = uidoc;
                _document = doc;
                _toposolid = toposolid;
                _center = center;
                _options = options;
                _applyCallback = callback;
            }
            ExternalEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            ModifyTopoInteractiveAction action;
            lock (_lock)
            {
                action = _action;
                _action = ModifyTopoInteractiveAction.None;
            }

            try
            {
                switch (action)
                {
                    case ModifyTopoInteractiveAction.PreviewTick:
                        ExecutePreviewTick();
                        break;
                    case ModifyTopoInteractiveAction.ClearPreview:
                        _surface?.Clear();
                        _uidoc?.RefreshActiveView();
                        break;
                    case ModifyTopoInteractiveAction.ApplyShapeByPoint:
                        ExecuteApply();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ModifyTopo interactive handler failed ({Action})", action);
                _statusCallback?.Invoke($"Preview error: {ex.Message}");
            }
        }

        private void ExecutePreviewTick()
        {
            if (_uidoc == null || _document == null || _toposolid == null ||
                _options == null || _surface == null || _session == null)
                return;

            if (ModifyTopoViewPickHelper.IsCursorOverExcludedWindow(_excludeHwnd))
            {
                _statusCallback?.Invoke("Move cursor into the 3D view (not on dialog).");
                return;
            }

            if (!ModifyTopoViewPickHelper.TryGetHitOnToposolid(
                    _uidoc, _toposolid, _session.Geometry, _excludeHwnd, out XYZ center, out ElementId viewId))
            {
                _statusCallback?.Invoke("Aim cursor at the toposolid in the 3D view.");
                return;
            }

            View view = _document.GetElement(viewId) as View ?? _document.ActiveView;
            _surface.EnsureBaseOverlay(_session.Geometry, view);

            var stampPts = _session.GetHoverStampPoints(center, _options);
            _surface.UpdatePreview(view, center, _options, stampPts);
            _statusCallback?.Invoke($"Preview active — click to apply (Gain {_options.ShapeDeltaFeet:F1} ft)");

            try { _uidoc.RefreshActiveView(); } catch { }

            if (_clickToApply)
                ExecuteApplyAt(center, view);
        }

        private void ExecuteApply()
        {
            if (_document == null || _toposolid == null || _center == null || _options == null)
                return;

            View view = _document.ActiveView;
            ExecuteApplyAt(_center, view);
        }

        private void ExecuteApplyAt(XYZ center, View view)
        {
            ModifyTopoResult result;
            using (Transaction tx = new Transaction(_document, "Shape by Point"))
            {
                tx.Start();
                result = ModifyTopoService.Instance.Apply(_document, _toposolid, _options, center);
                tx.Commit();
            }

            _session?.RefreshBaseFromToposolid(_toposolid);
            _session?.RecordStamp(center, _options);
            _applyCallback?.Invoke(result);
            _statusCallback?.Invoke(
                $"Applied — {result.PointsAfterModification} points (Modify Sub Element)");

            if (view != null && _options != null)
            {
                var stampPts = _session?.GetHoverStampPoints(center, _options);
                _surface?.UpdatePreview(view, center, _options, stampPts);
            }

            try { _uidoc?.RefreshActiveView(); } catch { }
        }

        public string GetName() => "Modify Topo Interactive Handler";
    }
#endif
}
