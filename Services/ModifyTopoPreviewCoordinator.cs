using System;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using effetopo.Models;
using effetopo.Views;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Shape-by-Point: stage stamps in draft memory + DirectShape preview; commit on Ok.
    /// </summary>
    internal sealed class ModifyTopoPreviewCoordinator : IDisposable
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoDialog _dialog;
        private readonly ModifyTopoSubElementSession _subElementSession;
        private readonly ModifyTopoDraftSession _draftSession;
        private readonly ModifyTopoPreviewSurface _previewSurface;
        private readonly ModifyTopoGeometrySurfaceCache _geometryCache;
        private readonly DispatcherTimer _timer;

        private string _lastStatus = string.Empty;
        private bool _pickInProgress;

        public ModifyTopoPreviewCoordinator(
            UIApplication uiApp, Toposolid toposolid, ModifyTopoDialog dialog)
        {
            _uidoc = uiApp.ActiveUIDocument;
            _doc = _uidoc.Document;
            _toposolid = toposolid;
            _dialog = dialog;
            _subElementSession = new ModifyTopoSubElementSession(_doc, _uidoc, toposolid);
            _draftSession = new ModifyTopoDraftSession(_doc, toposolid);
            _previewSurface = new ModifyTopoPreviewSurface(_doc);
            _geometryCache = new ModifyTopoGeometrySurfaceCache(toposolid);

            _timer = new DispatcherTimer(DispatcherPriority.Background, dialog.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _timer.Tick += OnTimerTick;
        }

        public bool HasPendingDraft => _draftSession?.HasPendingChanges == true;

        public ModifyTopoResult CommitDraftIfPending()
        {
            if (_draftSession == null || !_draftSession.HasPendingChanges)
                return null;

            _previewSurface.Clear();
            ModifyTopoResult result = _draftSession.Commit();
            try { _uidoc.RefreshActiveView(); } catch { }
            return result;
        }

        public void Start()
        {
            _dialog.RequestPickAndApplyStamp += OnRequestPickAndPreview;
            _dialog.RequestUndoDraftStamp += OnRequestUndoDraft;
            _dialog.LiveOptionsChanged += OnLiveOptionsChanged;
            _timer.Start();
            UpdateDraftUi();
            Log.Debug("ModifyTopo coordinator started (draft preview mode).");
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            if (_dialog != null)
            {
                _dialog.RequestPickAndApplyStamp -= OnRequestPickAndPreview;
                _dialog.RequestUndoDraftStamp -= OnRequestUndoDraft;
                _dialog.LiveOptionsChanged -= OnLiveOptionsChanged;
            }
            _previewSurface.Clear();
            _subElementSession.Dispose();
            try { _uidoc.RefreshActiveView(); } catch { }
        }

        private void OnLiveOptionsChanged(object sender, EventArgs e) => RefreshDraftPreview();

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_dialog == null || !_dialog.IsVisible || _pickInProgress)
                return;

            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint)
                return;
        }

        private void OnRequestUndoDraft(object sender, EventArgs e)
        {
            if (!_draftSession.TryUndoLastStamp())
            {
                SetStatus("Không có stamp nào để undo.");
                return;
            }

            UpdateDraftUi();
            RefreshDraftPreview();
            SetStatus($"Đã undo — còn {_draftSession.StampCount} stamp trong draft.");
        }

        private void OnRequestPickAndPreview(object sender, EventArgs e)
        {
            if (_pickInProgress || !_dialog.TryGetLiveOptions(out ModifyTopoOptions options))
                return;

            if (options.Tool != ModifyTopoTool.ShapeByPoint)
                return;

            _pickInProgress = true;
            try
            {
                _dialog.Hide();

                XYZ pick = _uidoc.Selection.PickPoint(
                    ObjectSnapTypes.Nearest | ObjectSnapTypes.Intersections,
                    "Chọn điểm trên Toposolid để preview stamp (Ok để áp dụng thật)");

                ModifyTopoDraftStampResult staged = _draftSession.StageStamp(pick, options);
                UpdateDraftUi();
                RefreshDraftPreview();

                SetStatus(
                    $"Draft #{staged.StampIndex}: +{staged.PointsAdded} điểm, " +
                    $"{staged.VerticesModified} đỉnh — tổng preview {_draftSession.DraftPointCount} điểm. Bấm Ok để commit.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                SetStatus("Đã hủy pick — thử lại.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pick and preview stamp failed");
                SetStatus($"Lỗi: {ex.Message}");
            }
            finally
            {
                _dialog.Show();
                _pickInProgress = false;
                try { _uidoc.RefreshActiveView(); } catch { }
            }
        }

        private void UpdateDraftUi()
        {
            _dialog.UpdatePointCounts(_draftSession.OriginalPointCount, _draftSession.DraftPointCount);
            _dialog.SetDraftStampCount(_draftSession.StampCount);
        }

        private void RefreshDraftPreview()
        {
            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint ||
                !options.ShowPreview ||
                !_draftSession.HasPendingChanges)
            {
                if (!_draftSession.HasPendingChanges)
                    _previewSurface.Clear();
                return;
            }

            View view = _doc.ActiveView;
            if (view == null) return;

            _previewSurface.EnsureBaseOverlay(_geometryCache, view);

            ModifyTopoDraftSession.StampRecord last = _draftSession.GetLastStamp();
            var previewPoints = _draftSession.GetAllPreviewPoints();
            if (last != null)
            {
                _previewSurface.UpdatePreview(view, last.Center, last.Options, previewPoints);
            }

            try { _uidoc.RefreshActiveView(); } catch { }
        }

        private void SetStatus(string message)
        {
            if (_lastStatus == message) return;
            _lastStatus = message ?? string.Empty;
            _dialog.SetPreviewStatus(_lastStatus);
        }
    }
#endif
}
