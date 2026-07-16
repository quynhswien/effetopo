using System;
using System.Linq;
using System.Windows.Interop;
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
    /// Shape-by-Point: TerrainModifier.Calculate() → preview render; commit on Ok.
    /// </summary>
    internal sealed class ModifyTopoPreviewCoordinator : IDisposable
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoDialog _dialog;
        private readonly ModifyTopoSubElementSession _subElementSession;
        private readonly ModifyTopoGeometrySurfaceCache _geometryCache;
        private readonly ModifyTopoDraftSession _draftSession;
        private readonly TerrainDirectContext3DPreview _dc3dPreview;
        private readonly TerrainMeshDirectShapePreview _directShapePreview;
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
            _geometryCache = new ModifyTopoGeometrySurfaceCache(toposolid);
            _draftSession = new ModifyTopoDraftSession(_doc, toposolid, _geometryCache);
            _dc3dPreview = TerrainDirectContext3DPreview.Instance;
            _dc3dPreview.BindSession(_doc, _uidoc);
            _dc3dPreview.EnsureRegistered();
            _directShapePreview = new TerrainMeshDirectShapePreview(_doc);

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

            ClearPreview();
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
            Log.Information("ModifyTopo coordinator started (TerrainModifier preview).");
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
            ClearPreview();
            _subElementSession.Dispose();
            try { _uidoc.RefreshActiveView(); } catch { }
        }

        private void ClearPreview()
        {
            _dc3dPreview.SetVisible(false);
            _dc3dPreview.SetMesh(null);
            View view = _doc.ActiveView;
            _directShapePreview.Clear(view);
        }

        private void OnLiveOptionsChanged(object sender, EventArgs e)
        {
            if (_dialog.TryGetLiveOptions(out ModifyTopoOptions options))
                _draftSession.UpdateLiveShapeOptions(options);
            RefreshDraftPreview();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_dialog == null || !_dialog.IsVisible || _pickInProgress)
                return;

            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint ||
                !options.ShowPreview)
                return;

            IntPtr dialogHwnd = new WindowInteropHelper(_dialog).Handle;
            if (!ModifyTopoViewPickHelper.TryGetHitOnToposolid(
                    _uidoc, _toposolid, _geometryCache, dialogHwnd,
                    out XYZ hoverCenter, out _))
                return;

            RefreshPreviewWithHover(hoverCenter, options);
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
            _timer.Stop();
            int stampsThisSession = 0;
            try
            {
                _dialog.Hide();

                while (true)
                {
                    if (!_dialog.TryGetLiveOptions(out options))
                        break;

                    try
                    {
                        string prompt = stampsThisSession == 0
                            ? "Chọn các điểm trên Toposolid để thêm stamp. Nhấn Esc khi xong."
                            : $"Đã thêm {stampsThisSession} stamp — chọn tiếp hoặc Esc để quay lại hộp thoại.";

                        XYZ pick = _uidoc.Selection.PickPoint(
                            ObjectSnapTypes.Nearest | ObjectSnapTypes.Intersections,
                            prompt);

                        ModifyTopoDraftStampResult staged = _draftSession.StageStamp(pick, options);
                        stampsThisSession++;
                        UpdateDraftUi();
                        RefreshDraftPreview();

                        Log.Information(
                            "Multi-pick stamp #{Index}: +{Added} pts, {Modified} verts",
                            staged.StampIndex, staged.PointsAdded, staged.VerticesModified);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }

                if (stampsThisSession > 0)
                {
                    SetStatus(
                        $"Đã thêm {stampsThisSession} stamp (tổng {_draftSession.StampCount}). " +
                        "Ok để ghi vào Toposolid, Pick tiếp, hoặc Cancel.");
                }
                else if (_draftSession.StampCount > 0)
                {
                    SetStatus(
                        $"Thoát chế độ pick — {_draftSession.StampCount} stamp trong draft. " +
                        "Ok / Pick tiếp / Cancel.");
                }
                else
                {
                    SetStatus("Thoát chế độ pick — chưa có stamp. Bấm Pick hoặc hover để preview.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Multi pick session failed");
                SetStatus($"Lỗi: {ex.Message}");
            }
            finally
            {
                _dialog.Show();
                _dialog.Activate();
                _pickInProgress = false;
                _timer.Start();
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
                    ClearPreview();
                return;
            }

            ShowCalculatedMesh(_draftSession.LastCalculated);
        }

        private void RefreshPreviewWithHover(XYZ hoverCenter, ModifyTopoOptions options)
        {
            var stampDefs = _draftSession.Stamps
                .Select(s => new TerrainModifier.StampDefinition { Center = s.Center, Options = s.Options })
                .ToList();

            if (hoverCenter != null)
            {
                stampDefs.Add(new TerrainModifier.StampDefinition
                {
                    Center = hoverCenter,
                    Options = options
                });
            }

            TerrainModifier.CalculateResult calculated = TerrainModifier.Calculate(
                _doc, _toposolid, _draftSession.GetBaseVertices(), stampDefs, _geometryCache);

            ShowCalculatedMesh(calculated);
        }

        private void ShowCalculatedMesh(TerrainModifier.CalculateResult calculated)
        {
            if (calculated == null)
                return;

            bool hasSolids = calculated.PreviewSolids != null && calculated.PreviewSolids.Count > 0;
            bool hasBrush = calculated.Mesh?.LineSegments.Count >= 2;

            if (!hasSolids && !hasBrush)
            {
                Log.Warning(
                    "Preview empty after Calculate (stamps={StampCount}, verts={VertCount})",
                    _draftSession.StampCount, calculated.Vertices?.Count ?? 0);
                return;
            }

            View view = _doc.ActiveView;

            if (hasBrush)
            {
                _dc3dPreview.SetMesh(calculated.Mesh);
                _dc3dPreview.SetVisible(true);
            }
            else
            {
                _dc3dPreview.SetMesh(null);
                _dc3dPreview.SetVisible(false);
            }

            if (hasSolids)
                _directShapePreview.UpdateFromRevitSolids(view, _toposolid.Id, calculated.PreviewSolids, calculated.Mesh);
            else
                _directShapePreview.Update(view, _toposolid.Id, calculated.Mesh);

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
