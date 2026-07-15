using System;
using System.Runtime.InteropServices;
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
    /// Shape-by-Point: SlabShapeEditor enabled + explicit PickPoint to apply stamp.
    /// </summary>
    internal sealed class ModifyTopoPreviewCoordinator : IDisposable
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly Toposolid _toposolid;
        private readonly ModifyTopoDialog _dialog;
        private readonly ModifyTopoSubElementSession _subElementSession;
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

            _timer = new DispatcherTimer(DispatcherPriority.Background, dialog.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            _dialog.RequestPickAndApplyStamp += OnRequestPickAndApply;
            _dialog.LiveOptionsChanged += OnLiveOptionsChanged;
            _timer.Start();
            EnsureShapeByPointMode();
            Log.Debug("ModifyTopo coordinator started (PickPoint apply mode).");
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            if (_dialog != null)
            {
                _dialog.RequestPickAndApplyStamp -= OnRequestPickAndApply;
                _dialog.LiveOptionsChanged -= OnLiveOptionsChanged;
            }
            _subElementSession.Dispose();
            try { _uidoc.RefreshActiveView(); } catch { }
        }

        private void OnLiveOptionsChanged(object sender, EventArgs e) => EnsureShapeByPointMode();

        private void EnsureShapeByPointMode()
        {
            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint)
                return;

            if (_subElementSession.TryEnable())
                SetStatus("Bấm «Pick & Apply Stamp» rồi chọn điểm trên toposolid.");
            else
                SetStatus("Could not enable Modify Sub Elements on this Toposolid.");
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_dialog == null || !_dialog.IsVisible || _pickInProgress)
                return;

            if (!_dialog.TryGetLiveOptions(out ModifyTopoOptions options) ||
                options.Tool != ModifyTopoTool.ShapeByPoint)
                return;

            _subElementSession.TryEnable();
        }

        private void OnRequestPickAndApply(object sender, EventArgs e)
        {
            if (_pickInProgress || !_dialog.TryGetLiveOptions(out ModifyTopoOptions options))
                return;

            if (options.Tool != ModifyTopoTool.ShapeByPoint)
                return;

            _pickInProgress = true;
            try
            {
                _dialog.Hide();
                _subElementSession.TryEnable();

                XYZ pick = _uidoc.Selection.PickPoint(
                    ObjectSnapTypes.Nearest | ObjectSnapTypes.Intersections,
                    "Click a point on the Toposolid surface to apply the stamp");

                ApplyShapeByPoint(pick, options);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                SetStatus("Pick cancelled — try again.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pick and apply stamp failed");
                SetStatus($"Error: {ex.Message}");
            }
            finally
            {
                _dialog.Show();
                _pickInProgress = false;
                try { _uidoc.RefreshActiveView(); } catch { }
            }
        }

        private void ApplyShapeByPoint(XYZ center, ModifyTopoOptions options)
        {
            ModifyTopoResult result;
            using (Transaction tx = new Transaction(_doc, "Shape by Point"))
            {
                tx.Start();
                result = ModifyTopoService.Instance.Apply(_doc, _toposolid, options, center);
                tx.Commit();
            }

            _subElementSession.TryEnable();
            _dialog.UpdatePointCounts(result.OriginalPointCount, result.PointsAfterModification);
            SetStatus(
                $"Applied — {result.VerticesModified} vertices updated, {result.PointsAdded} added, " +
                $"{result.PointsAfterModification} total points");

            Log.Information(
                "Shape by Point at ({X:F2},{Y:F2}): modified={Modified}, added={Added}, total={Total}",
                center.X, center.Y, result.VerticesModified, result.PointsAdded, result.PointsAfterModification);
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
