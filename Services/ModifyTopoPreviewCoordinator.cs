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

            _dc3dPreview = TerrainDirectContext3DPreview.Instance;

            _dc3dPreview.BindSession(_doc, _uidoc);

            _dc3dPreview.EnsureRegistered();

            _directShapePreview = new TerrainMeshDirectShapePreview(_doc);

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

                _draftSession.UpdateLiveShapeOptions(options);

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
            int linesThisSession = 0;
            int originalCount = ModifyTopoService.Instance.CountSlabShapeVertices(_toposolid);

            try
            {
                _dialog.Hide();

                while (true)
                {
                    if (!_dialog.TryGetLiveOptions(out options))
                        break;

                    try
                    {
                        string prompt = linesThisSession == 0
                            ? "Select model lines or splines to shape the Toposolid. Press Esc when done."
                            : $"Applied {linesThisSession} line(s) — select another line or Esc to return.";

                        Reference reference = _uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new ModelCurveSelectionFilter(),
                            prompt);

                        Element element = _doc.GetElement(reference);
                        if (element is not ModelCurve modelCurve)
                            continue;

                        Curve curve = modelCurve.GeometryCurve;
                        if (curve == null)
                        {
                            SetStatus("Selected element has no curve geometry.");
                            continue;
                        }

                        ModifyTopoResult result;
                        using (Transaction tx = new Transaction(_doc, "Shape By Line"))
                        {
                            tx.Start();
                            try
                            {
                                result = ModifyTopoService.Instance.ApplyShapeByLine(
                                    _doc, _toposolid, curve, options);
                                tx.Commit();
                            }
                            catch (Exception ex)
                            {
                                tx.RollBack();
                                Log.Warning(ex, "Shape by Line failed");
                                SetStatus($"Lỗi: {ex.Message}");
                                continue;
                            }
                        }

                        linesThisSession++;
                        _dialog.UpdatePointCounts(originalCount, result.PointsAfterModification);
                        SetStatus(
                            $"Line {linesThisSession}: +{result.PointsAdded} points, {result.VerticesModified} updated. Esc để quay lại.");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }

                if (linesThisSession > 0)
                    SetStatus($"Đã áp dụng {linesThisSession} line(s). Ok để đóng hoặc Pick Lines tiếp.");
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
                try { _uidoc.RefreshActiveView(); } catch { }
            }
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



            ShowCalculatedMesh(_draftSession.LastCalculated, _doc.ActiveView?.Id ?? ElementId.InvalidElementId);

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


