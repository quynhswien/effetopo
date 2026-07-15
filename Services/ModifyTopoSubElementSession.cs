using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Activates native Revit Modify Sub Elements on Toposolid (SlabShapeEditor.Enable).
    /// Points render on the real surface — no DirectShape overlay needed.
    /// </summary>
    internal sealed class ModifyTopoSubElementSession : IDisposable
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly Toposolid _toposolid;
        private bool _enabled;

        public ModifyTopoSubElementSession(Document doc, UIDocument uidoc, Toposolid toposolid)
        {
            _doc = doc;
            _uidoc = uidoc;
            _toposolid = toposolid;
        }

        public bool IsEnabled => _enabled;

        public bool TryEnable()
        {
            if (_enabled)
                return true;

            SlabShapeEditor editor = _toposolid?.GetSlabShapeEditor();
            if (editor == null)
            {
                Log.Warning("SlabShapeEditor unavailable on Toposolid {Id}", _toposolid?.Id);
                return false;
            }

            try
            {
                using (Transaction tx = new Transaction(_doc, "Enable Modify Sub Elements"))
                {
                    tx.Start();
                    editor.Enable();
                    _doc.Regenerate();
                    tx.Commit();
                }

                _uidoc.Selection.SetElementIds(new List<ElementId> { _toposolid.Id });
                try { _uidoc.ShowElements(_toposolid.Id); } catch { }
                _uidoc.RefreshActiveView();
                _enabled = true;

                int count = ModifyTopoService.Instance.CountSlabShapeVertices(_toposolid);
                Log.Information("Modify Sub Elements enabled on Toposolid ({PointCount} points visible)", count);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enable Modify Sub Elements");
                return false;
            }
        }

        public void RefreshView()
        {
            if (!_enabled) return;
            try { _uidoc.RefreshActiveView(); } catch { }
        }

        public void Dispose()
        {
            if (!_enabled)
                return;

            _enabled = false;
            try
            {
                _uidoc.RefreshActiveView();
            }
            catch (Exception ex)
            {
                Log.Debug("Sub-element session cleanup: {Error}", ex.Message);
            }
        }
    }
#endif
}
