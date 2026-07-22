using System;

using System.Linq;

using System.Windows.Interop;

using Autodesk.Revit.DB;

using Autodesk.Revit.UI;

using Autodesk.Revit.UI.Events;

using Autodesk.Revit.UI.Selection;

using effetopo.Models;

using effetopo.Views;



namespace effetopo.Services

{

#if REVIT2024_OR_GREATER

    /// <summary>

    /// Shape-by-Point: TerrainModifier.Calculate() → preview render; commit on Ok.

    /// Hover preview runs on Revit Idling (API-safe), not WPF DispatcherTimer.

    /// </summary>

    internal sealed class ModifyTopoPreviewCoordinator : IDisposable

    {

        private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(200);



        private readonly UIApplication _uiApp;

        private readonly UIDocument _uidoc;

        private readonly Document _doc;

        private readonly Toposolid _toposolid;

        private readonly ModifyTopoDialog _dialog;

        private readonly ModifyTopoSubElementSession _subElementSession;

        private readonly ModifyTopoGeometrySurfaceCache _geometryCache;

        private readonly ModifyTopoDraftSession _draftSession;

        private readonly ModifyTopoLineDraftSession _lineDraftSession;

        private readonly TerrainDirectContext3DPreview _dc3dPreview;

        private readonly TerrainMeshDirectShapePreview _directShapePreview;



        private string _lastStatus = string.Empty;

        private string _lastHoverKey = string.Empty;

        private DateTime _lastHoverUtc = DateTime.MinValue;

        private bool _pickInProgress;



        public ModifyTopoPreviewCoordinator(

            UIApplication uiApp, Toposolid toposolid, ModifyTopoDialog dialog)

        {

            _uiApp = uiApp;

            _uidoc = uiApp.ActiveUIDocument;

            _doc = _uidoc.Document;

            _toposolid = toposolid;

            _dialog = dialog;

            _subElementSession = new ModifyTopoSubElementSession(_doc, _uidoc, toposolid);

            _geometryCache = new ModifyTopoGeometrySurfaceCache(toposolid);

            _draftSession = new ModifyTopoDraftSession(_doc, toposolid, _geometryCache);

            _lineDraftSession = new ModifyTopoLineDraftSession(_doc, toposolid);

            _dc3dPreview = TerrainDirectContext3DPreview.Instance;

            _dc3dPreview.BindSession(_doc, _uidoc);

            _dc3dPreview.EnsureRegistered();

            _directShapePreview = new TerrainMeshDirectShapePreview(_doc);

        }



        public bool HasPendingDraft =>
            _draftSession?.HasPendingChanges == true || _lineDraftSession?.HasPendingChanges == true;



        public ModifyTopoResult CommitDraftIfPending(ModifyTopoTool tool)

        {

            if (tool == ModifyTopoTool.ShapeByLine && _lineDraftSession?.HasPendingChanges == true)

            {

                ClearPreview();

                ModifyTopoResult result = _lineDraftSession.Commit();

                try { _uidoc.RefreshActiveView(); } catch { }

                return result;

            }



            if (tool == ModifyTopoTool.ShapeByPoint && _draftSession?.HasPendingChanges == true)

            {

                ClearPreview();

                ModifyTopoResult result = _draftSession.Commit();

                try { _uidoc.RefreshActiveView(); } catch { }

                return result;

            }



            return null;

        }



        public void Start()

        {

            _dialog.RequestPickAndApplyStamp += OnRequestPickAndPreview;

            _dialog.RequestPickAndApplyLines += OnRequestPickAndApplyLines;

            _dialog.RequestUndoDraftStamp += OnRequestUndoDraft;

            _dialog.LiveOptionsChanged += OnLiveOptionsChanged;

            _uiApp.Idling += OnIdling;

            UpdateDraftUi();

            Log.Information("ModifyTopo coordinator started (TerrainModifier preview).");

        }



        public void Dispose()

        {

            _uiApp.Idling -= OnIdling;

            if (_dialog != null)

            {

                _dialog.RequestPickAndApplyStamp -= OnRequestPickAndPreview;

                _dialog.RequestPickAndApplyLines -= OnRequestPickAndApplyLines;

                _dialog.RequestUndoDraftStamp -= OnRequestUndoDraft;

                _dialog.LiveOptionsChanged -= OnLiveOptionsChanged;

            }

            ClearPreview();

            _subElementSession.Dispose();

            try { _uidoc.RefreshActiveView(); } catch { }

        }



        private void ClearPreview()

        {

            _lastHoverKey = string.Empty;

            _dc3dPreview.SetVisible(false);

            _dc3dPreview.SetMesh(null);

            View view = _doc.ActiveView;

            _directShapePreview.Clear(view);

        }



        private void OnLiveOptionsChanged(object sender, EventArgs e)

        {

            _lastHoverKey = string.Empty;

            if (_dialog.TryGetLiveOptions(out ModifyTopoOptions options))

            {

                if (options.Tool == ModifyTopoTool.ShapeByPoint)

                    _draftSession.UpdateLiveShapeOptions(options);

                else if (options.Tool == ModifyTopoTool.ShapeByLine)

                    _lineDraftSession.UpdateLiveLineOptions(options);

            }

            RefreshDraftPreview();

        }



        private void OnIdling(object sender, IdlingEventArgs e)

        {

            if (_dialog == null || !_dialog.IsVisible || _pickInProgress)

                return;



            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||

                options.Tool != ModifyTopoTool.ShapeByPoint ||

                !options.ShowPreview)

                return;



            e.SetRaiseWithoutDelay();



            DateTime now = DateTime.UtcNow;

            if (now - _lastHoverUtc < HoverInterval)

                return;

            _lastHoverUtc = now;



            IntPtr dialogHwnd = new WindowInteropHelper(_dialog).Handle;

            if (!ModifyTopoViewPickHelper.TryGetHitOnToposolid(

                    _uidoc, _toposolid, _geometryCache, dialogHwnd,

                    out XYZ hoverCenter, out ElementId viewId))

            {

                _lastHoverKey = string.Empty;

                if (_draftSession.HasPendingChanges)

                    RefreshDraftPreview();

                else

                    ClearPreview();

                return;

            }



            string hoverKey = BuildHoverKey(hoverCenter, options, _draftSession.StampCount);

            if (hoverKey == _lastHoverKey)

                return;

            _lastHoverKey = hoverKey;



            RefreshPreviewWithHover(hoverCenter, options, viewId);

            SetStatus($"Hover preview — Gain {options.ShapeDeltaFeet:F1} ft, radius {options.ShapeRadiusFeet:F1} ft");

        }



        private static string BuildHoverKey(XYZ center, ModifyTopoOptions options, int stampCount) =>

            $"{center.X:F2}:{center.Y:F2}:{options.ShapeRadiusFeet:F2}:{options.ShapeDeltaFeet:F2}:{options.ShapePointDensity}:{stampCount}";



        private void OnRequestPickAndApplyLines(object sender, EventArgs e)
        {
            if (_pickInProgress || !_dialog.TryGetLiveOptions(out ModifyTopoOptions options))
                return;

            if (options.Tool != ModifyTopoTool.ShapeByLine)
                return;

            _pickInProgress = true;
            int totalLinesThisSession = 0;
            bool usePreview = options.ShowPreview;
            var pickedThisSession = new HashSet<ElementId>();
            var filter = new ModelCurveSelectionFilter();

            try
            {
                _dialog.Hide();

                try
                {
                    ICollection<Element> windowPicked = _uidoc.Selection.PickElementsByRectangle(
                        filter,
                        "Kéo cửa sổ chọn nhiều line, hoặc Esc để click từng line (Tab chuyển highlight).");

                    var windowIds = windowPicked
                        .Select(element => element?.Id)
                        .Where(id => id != null && id != ElementId.InvalidElementId)
                        .ToList();

                    totalLinesThisSession += StagePickedLines(
                        windowIds, options, usePreview, pickedThisSession);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // User skipped window select — continue to click pick.
                }

                while (true)
                {
                    if (!_dialog.TryGetLiveOptions(out options))
                        break;

                    usePreview = options.ShowPreview;

                    try
                    {
                        string prompt = totalLinesThisSession == 0
                            ? "Click model line / spline (Tab chuyển highlight). Esc khi xong."
                            : $"Đã chọn {totalLinesThisSession} line — click thêm hoặc Esc quay lại hộp thoại.";

                        Reference reference = _uidoc.Selection.PickObject(ObjectType.Element, filter, prompt);
                        totalLinesThisSession += StagePickedLines(
                            new[] { reference.ElementId },
                            options,
                            usePreview,
                            pickedThisSession);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }

                if (totalLinesThisSession > 0)
                {
                    if (usePreview)
                        SetStatus(
                            $"Đã stage {totalLinesThisSession} line(s) trong preview. Ok để ghi vào Toposolid.");
                    else
                        SetStatus($"Đã áp dụng {totalLinesThisSession} line(s). Ok để đóng hoặc Pick Lines tiếp.");
                }
                else
                    SetStatus("Thoát chế độ pick — chưa chọn line nào.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Shape by Line pick session failed");
                SetStatus($"Lỗi: {ex.Message}");
            }
            finally
            {
                _dialog.Show();
                _dialog.Activate();
                _pickInProgress = false;
                RefreshLineDraftPreview();
                try { _uidoc.RefreshActiveView(); } catch { }
            }
        }

        private int StagePickedLines(
            IList<ElementId> lineIds,
            ModifyTopoOptions options,
            bool usePreview,
            ISet<ElementId> pickedThisSession)
        {
            if (lineIds == null || lineIds.Count == 0 || options == null || pickedThisSession == null)
                return 0;

            var newIds = new List<ElementId>();
            foreach (ElementId id in lineIds)
            {
                if (id == null || id == ElementId.InvalidElementId)
                    continue;
                if (usePreview && _lineDraftSession.ContainsCurve(id))
                    continue;
                if (!pickedThisSession.Add(id))
                    continue;

                newIds.Add(id);
            }

            if (newIds.Count == 0)
                return 0;

            if (usePreview)
            {
                ModifyTopoDraftStampResult staged = _lineDraftSession.StageCurves(newIds, options);
                UpdateLineDraftUi();
                RefreshLineDraftPreview();

                SetStatus(
                    $"Đã chọn {_lineDraftSession.LineCount} line (+{staged.PointsAdded} điểm preview). " +
                    "Click thêm hoặc Esc quay lại hộp thoại.");
            }
            else
            {
                ModifyTopoResult result = ApplyCurvesImmediate(newIds, options);
                _dialog.UpdatePointCounts(
                    result.OriginalPointCount,
                    result.PointsAfterModification);
                SetStatus(
                    $"Đã áp dụng {pickedThisSession.Count} line (+{result.PointsAdded} điểm). " +
                    "Click thêm hoặc Esc quay lại hộp thoại.");
            }

            return newIds.Count;
        }

        private ModifyTopoResult ApplyCurvesImmediate(IList<ElementId> curveIds, ModifyTopoOptions options)
        {
            ModifyTopoResult last = null;
            using (Transaction tx = new Transaction(_doc, "Shape By Line"))
            {
                tx.Start();
                try
                {
                    foreach (ElementId id in curveIds)
                    {
                        if (_doc.GetElement(id) is not ModelCurve modelCurve)
                            continue;

                        Curve curve = modelCurve.GeometryCurve;
                        if (curve == null)
                            continue;

                        last = ModifyTopoService.Instance.ApplyShapeByLine(
                            _doc, _toposolid, curve, options);
                    }

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    Log.Warning(ex, "Shape by Line failed");
                    throw;
                }
            }

            return last ?? new ModifyTopoResult
            {
                OriginalPointCount = ModifyTopoService.Instance.CountSlabShapeVertices(_toposolid),
                PointsAfterModification = ModifyTopoService.Instance.CountSlabShapeVertices(_toposolid)
            };
        }

        private sealed class ModelCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is not ModelCurve modelCurve)
                    return false;

                Curve curve = modelCurve.GeometryCurve;
                return curve is Line || curve is Arc || curve is NurbSpline || curve is HermiteSpline;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private void OnRequestUndoDraft(object sender, EventArgs e)

        {

            _lastHoverKey = string.Empty;

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

            _lastHoverKey = string.Empty;

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

                _lastHoverKey = string.Empty;

                try { _uidoc.RefreshActiveView(); } catch { }

            }

        }



        private void UpdateDraftUi()

        {

            _dialog.UpdatePointCounts(_draftSession.OriginalPointCount, _draftSession.DraftPointCount);

            _dialog.SetDraftStampCount(_draftSession.StampCount);

        }



        private void UpdateLineDraftUi()

        {

            _dialog.UpdatePointCounts(_lineDraftSession.OriginalPointCount, _lineDraftSession.DraftPointCount);

            _dialog.SetDraftStampCount(_lineDraftSession.LineCount);

        }



        private void RefreshDraftPreview()

        {

            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options))

                return;



            if (options.Tool == ModifyTopoTool.ShapeByPoint)

            {

                RefreshStampDraftPreview(options);

                return;

            }



            if (options.Tool == ModifyTopoTool.ShapeByLine)

                RefreshLineDraftPreview();

        }



        private void RefreshStampDraftPreview(ModifyTopoOptions options)

        {

            if (!options.ShowPreview || !_draftSession.HasPendingChanges)

            {

                if (!_draftSession.HasPendingChanges && !_lineDraftSession.HasPendingChanges)

                    ClearPreview();

                return;

            }



            ShowCalculatedMesh(_draftSession.LastCalculated, _doc.ActiveView?.Id ?? ElementId.InvalidElementId);

        }



        private void RefreshLineDraftPreview()

        {

            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||

                options.Tool != ModifyTopoTool.ShapeByLine ||

                !options.ShowPreview ||

                !_lineDraftSession.HasPendingChanges)

            {

                if (!_draftSession.HasPendingChanges && !_lineDraftSession.HasPendingChanges)

                    ClearPreview();

                return;

            }



            ShowCalculatedMesh(_lineDraftSession.LastCalculated, _doc.ActiveView?.Id ?? ElementId.InvalidElementId);

        }



        private void RefreshPreviewWithHover(XYZ hoverCenter, ModifyTopoOptions options, ElementId viewId)

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



            ShowCalculatedMesh(calculated, viewId);

        }



        private void ShowCalculatedMesh(TerrainModifier.CalculateResult calculated, ElementId viewId)

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



            View view = _doc.GetElement(viewId) as View ?? _doc.ActiveView;

            if (view == null)

                return;



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


