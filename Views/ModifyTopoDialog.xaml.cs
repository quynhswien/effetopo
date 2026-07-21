using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using effetopo.Models;
using effetopo.Services;

namespace effetopo.Views
{
    public partial class ModifyTopoDialog : Window
    {
        private readonly bool _useMillimeters;
        private readonly string _lengthUnit;
        private ModifyTopoTool _selectedTool = ModifyTopoTool.InflateSurface;

        public event EventHandler LiveOptionsChanged;
        public event EventHandler RequestPickAndApplyStamp;
        public event EventHandler RequestPickAndApplyLines;
        public event EventHandler RequestUndoDraftStamp;

        public ModifyTopoOptions? SelectedOptions { get; private set; }

        /// <summary>True when user clicked Apply (keep dialog open); false when Ok or first open.</summary>
        public bool IsApplyAction { get; private set; }

        /// <summary>True when dialog should close after this action.</summary>
        public bool CloseAfterAction { get; private set; }

        public ModifyTopoDialog(
            bool useMillimeters,
            int originalPointCount,
            int currentPointCount,
            ModifyTopoSettings? initialSettings = null)
        {
            InitializeComponent();
            _useMillimeters = useMillimeters;
            _lengthUnit = useMillimeters ? "m" : "ft";

            OriginalPointsText.Text = originalPointCount.ToString(CultureInfo.InvariantCulture);
            ModifiedPointsText.Text = currentPointCount.ToString(CultureInfo.InvariantCulture);

            SetupUnits();
            PopulateFalloffCombos();
            PopulateSmoothAlgoCombo();
            SetupLineSamplingUi();
            WireLivePreviewEvents();

            ShapePointDensitySlider.ValueChanged += (_, e) =>
            {
                ShapePointDensityText.Text = $"{e.NewValue:F0}";
                RaiseLiveOptionsChanged();
            };

            RotationSlider.ValueChanged += (_, e) =>
            {
                RotationValueText.Text = $"{e.NewValue:F0}°";
                RaiseLiveOptionsChanged();
            };

            if (initialSettings != null)
                ApplySettings(initialSettings);
            else
                SetDefaults();

            SelectTool(_selectedTool);
            SetPreviewStatus("Di chuột lên Toposolid trong view 3D để xem preview stamp.");
        }

        /// <summary>Modeless dialog result: true = Ok, false = Cancel, null = still open.</summary>
        private bool? _modelessResult;

        /// <summary>Modeless show so Revit view stays interactive for hover preview.</summary>
        public bool? ShowModelessAndWait()
        {
            bool? result = null;
            bool done = false;
            Closed += (_, __) => { done = true; result = _modelessResult; };
            Show();

            // Short frame pumps let DispatcherTimer run without blocking Revit indefinitely.
            while (!done)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new DispatcherOperationCallback(_ =>
                    {
                        frame.Continue = false;
                        return null;
                    }),
                    null);
                Dispatcher.PushFrame(frame);
            }

            return result;
        }

        public bool TryGetLiveOptions(out ModifyTopoOptions options)
        {
            options = null;
            try
            {
                var settings = BuildSettingsFromUi(validateStrict: false);
                options = settings.ToOptions(_useMillimeters);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void UpdatePointCounts(int originalCount, int currentCount)
        {
            OriginalPointsText.Text = originalCount.ToString(CultureInfo.InvariantCulture);
            ModifiedPointsText.Text = currentCount.ToString(CultureInfo.InvariantCulture);
        }

        public void SetPreviewStatus(string message)
        {
            if (PreviewStatusText != null)
                PreviewStatusText.Text = message ?? string.Empty;
        }

        public void SetDraftStampCount(int count)
        {
            UndoBtn.IsEnabled = count > 0;
        }

        private void WireLivePreviewEvents()
        {
            void Hook(System.Windows.Controls.Control c)
            {
                if (c is TextBox tb)
                    tb.TextChanged += (_, __) => RaiseLiveOptionsChanged();
                else if (c is ComboBox cb)
                    cb.SelectionChanged += (_, __) => RaiseLiveOptionsChanged();
                else if (c is System.Windows.Controls.CheckBox chk)
                {
                    chk.Checked += (_, __) => RaiseLiveOptionsChanged();
                    chk.Unchecked += (_, __) => RaiseLiveOptionsChanged();
                }
            }

            Hook(CellSizeBox);
            Hook(MeshDensityBox);
            Hook(ShapeRadiusBox);
            Hook(ShapeDeltaBox);
            Hook(ShowPreviewCheck);
            Hook(ShapeFalloffCombo);
            ShapePointDensitySlider.ValueChanged += (_, __) => RaiseLiveOptionsChanged();
            SmoothStrengthSlider.ValueChanged += (_, __) => RaiseLiveOptionsChanged();
        }

        private void SetupLineSamplingUi()
        {
            if (_useMillimeters)
            {
                LineDistPreset1.Content = "300 mm (Default)";
                LineDistPreset2.Content = "500 mm";
                LineDistPreset3.Content = "1000 mm";
                LineDistUnitText.Text = "mm";
                LineDistCustomTextBox.Text = "300";
            }
            else
            {
                LineDistPreset1.Content = "1' (Default)";
                LineDistPreset2.Content = "2'";
                LineDistPreset3.Content = "4'";
                LineDistUnitText.Text = "feet";
                LineDistCustomTextBox.Text = "1";
            }

            LineCountPreset1.Content = "5 segments (6 points)";
            LineCountPreset2.Content = "10 segments (11 points)";
            LineCountPreset3.Content = "20 segments (21 points)";
            LineCountCustomTextBox.Text = "10";

            LineDistanceModeOption.Checked += (_, __) => SetLineModePanels(true);
            LineCountModeOption.Checked += (_, __) => SetLineModePanels(false);

            LineDistCustomOption.Checked += (_, __) => LineDistCustomTextBox.IsEnabled = true;
            LineDistPreset1.Checked += (_, __) => LineDistCustomTextBox.IsEnabled = false;
            LineDistPreset2.Checked += (_, __) => LineDistCustomTextBox.IsEnabled = false;
            LineDistPreset3.Checked += (_, __) => LineDistCustomTextBox.IsEnabled = false;

            LineCountCustomOption.Checked += (_, __) => LineCountCustomTextBox.IsEnabled = true;
            LineCountPreset1.Checked += (_, __) => LineCountCustomTextBox.IsEnabled = false;
            LineCountPreset2.Checked += (_, __) => LineCountCustomTextBox.IsEnabled = false;
            LineCountPreset3.Checked += (_, __) => LineCountCustomTextBox.IsEnabled = false;
        }

        private void SetLineModePanels(bool distanceMode)
        {
            if (LineDistancePanel == null || LineCountPanel == null)
                return;

            LineDistancePanel.IsEnabled = distanceMode;
            LineCountPanel.IsEnabled = !distanceMode;
        }

        private void RaiseLiveOptionsChanged() => LiveOptionsChanged?.Invoke(this, EventArgs.Empty);

        private void SetupUnits()
        {
            string u = _lengthUnit;
            InflateRadiusUnit.Text = u;
            InflateHeightUnit.Text = u;
            ShapeRadiusUnit.Text = u;
            ShapeDeltaUnit.Text = u;
            CellSizeUnit.Text = u;
            MeshDensityUnit.Text = u;
        }

        private void SetDefaults()
        {
            if (_useMillimeters)
            {
                CellSizeBox.Text = "1.00";
                MeshDensityBox.Text = "7.50";
                InflateRadiusBox.Text = "10.00";
                InflateHeightBox.Text = "1.00";
                ShapeRadiusBox.Text = "10.00";
                ShapeDeltaBox.Text = "2.00";
            }
            else
            {
                CellSizeBox.Text = "3.28";
                MeshDensityBox.Text = "24.61";
                InflateRadiusBox.Text = "32.81";
                InflateHeightBox.Text = "3.28";
                ShapeRadiusBox.Text = "32.81";
                ShapeDeltaBox.Text = "6.56";
            }
            ShowPreviewCheck.IsChecked = true;
            ShapePointDensitySlider.Value = 5;
            ShapePointDensityText.Text = "5";
            CurvatureThresholdBox.Text = "0.02";
            SmoothIterationsBox.Text = "3";
        }

        private void PopulateFalloffCombos()
        {
            var items = Enum.GetValues(typeof(SculptFalloffType))
                .Cast<SculptFalloffType>()
                .Select(f => new ComboBoxItem { Content = f.ToString(), Tag = f })
                .ToArray();
            InflateFalloffCombo.ItemsSource = items;
            InflateFalloffCombo.SelectedIndex = 0;
            ShapeFalloffCombo.ItemsSource = items.Select(i => new ComboBoxItem
            {
                Content = ((ComboBoxItem)i).Content,
                Tag = ((ComboBoxItem)i).Tag
            }).ToArray();
            ShapeFalloffCombo.SelectedIndex = 2; // Smooth
        }

        private void PopulateSmoothAlgoCombo()
        {
            SmoothAlgoCombo.ItemsSource = Enum.GetValues(typeof(SmoothAlgorithm))
                .Cast<SmoothAlgorithm>()
                .Select(a => new ComboBoxItem { Content = a.ToString(), Tag = a })
                .ToArray();
            SmoothAlgoCombo.SelectedIndex = 1; // Taubin
        }

        private void ApplySettings(ModifyTopoSettings settings)
        {
            CellSizeBox.Text = settings.CellSizeDisplay.ToString(CultureInfo.InvariantCulture);
            MeshDensityBox.Text = settings.MeshDensityDisplay.ToString(CultureInfo.InvariantCulture);
            RotationSlider.Value = settings.RotationDegrees;
            ModifyBoundaryCheck.IsChecked = settings.ModifyBoundary;
            ShowPreviewCheck.IsChecked = settings.ShowPreview;

            InflateRadiusBox.Text = settings.InflateRadiusDisplay.ToString(CultureInfo.InvariantCulture);
            InflateHeightBox.Text = settings.InflateHeightDisplay.ToString(CultureInfo.InvariantCulture);
            SelectComboByTag(InflateFalloffCombo, settings.InflateFalloff);

            ShapeRadiusBox.Text = settings.ShapeRadiusDisplay.ToString(CultureInfo.InvariantCulture);
            ShapeDeltaBox.Text = settings.ShapeDeltaDisplay.ToString(CultureInfo.InvariantCulture);
            ShapePointDensitySlider.Value = Math.Max(1, Math.Min(settings.ShapePointDensity, 10));
            ShapePointDensityText.Text = $"{ShapePointDensitySlider.Value:F0}";
            SelectComboByTag(ShapeFalloffCombo, settings.ShapeFalloff);

            SelectComboByTag(SmoothAlgoCombo, settings.SmoothAlgorithm);
            SmoothIterationsBox.Text = settings.SmoothIterations.ToString(CultureInfo.InvariantCulture);
            SmoothStrengthSlider.Value = settings.SmoothStrength;
            CurvatureThresholdBox.Text = settings.CurvatureThreshold.ToString(CultureInfo.InvariantCulture);
            RemeshEntireCheck.IsChecked = settings.RemeshEntireSurface;

            ApplyLineSamplingSettings(settings);

            SelectTool(settings.LastTool);
        }

        private void ApplyLineSamplingSettings(ModifyTopoSettings settings)
        {
            if (settings.LineSampleMode == BoundarySampleMode.BySegmentCount)
            {
                LineCountModeOption.IsChecked = true;
                SetLineModePanels(false);

                if (settings.LineUseCustomSegmentCount)
                {
                    LineCountCustomOption.IsChecked = true;
                    LineCountCustomTextBox.Text = settings.LineCustomSegmentCount.ToString(CultureInfo.InvariantCulture);
                }
                else if (settings.LineSegmentPresetIndex == 2)
                    LineCountPreset3.IsChecked = true;
                else if (settings.LineSegmentPresetIndex == 1)
                    LineCountPreset2.IsChecked = true;
                else
                    LineCountPreset1.IsChecked = true;
            }
            else
            {
                LineDistanceModeOption.IsChecked = true;
                SetLineModePanels(true);

                if (settings.LineUseCustomSpacing)
                {
                    LineDistCustomOption.IsChecked = true;
                    LineDistCustomTextBox.Text = settings.LineCustomSpacingDisplay.ToString(CultureInfo.InvariantCulture);
                }
                else if (settings.LineDistancePresetIndex == 1)
                    LineDistPreset2.IsChecked = true;
                else if (settings.LineDistancePresetIndex == 2)
                    LineDistPreset3.IsChecked = true;
                else
                    LineDistPreset1.IsChecked = true;
            }
        }

        private static void SelectComboByTag(ComboBox combo, Enum value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag is Enum e && e.Equals(value))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton btn || btn.Tag is not string tag) return;
            if (!Enum.TryParse(tag, out ModifyTopoTool tool)) return;
            SelectTool(tool);
        }

        private void SelectTool(ModifyTopoTool tool)
        {
            _selectedTool = tool;
            InflateToolBtn.IsChecked = tool == ModifyTopoTool.InflateSurface;
            MeshToolBtn.IsChecked = tool == ModifyTopoTool.MeshControl;
            ShapeToolBtn.IsChecked = tool == ModifyTopoTool.ShapeByPoint;
            ShapeLineToolBtn.IsChecked = tool == ModifyTopoTool.ShapeByLine;
            SmoothToolBtn.IsChecked = tool == ModifyTopoTool.SmoothGeometry;

            InflatePanel.Visibility = tool == ModifyTopoTool.InflateSurface ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            MeshPanel.Visibility = tool == ModifyTopoTool.MeshControl ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ShapePanel.Visibility = tool == ModifyTopoTool.ShapeByPoint ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ShapeLinePanel.Visibility = tool == ModifyTopoTool.ShapeByLine ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SmoothPanel.Visibility = tool == ModifyTopoTool.SmoothGeometry ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            UpdateToolDependentControls(tool);
            RaiseLiveOptionsChanged();
        }

        private void UpdateToolDependentControls(ModifyTopoTool tool)
        {
            bool meshTool = tool == ModifyTopoTool.MeshControl;
            bool shapeTool = tool == ModifyTopoTool.ShapeByPoint;
            bool shapeLineTool = tool == ModifyTopoTool.ShapeByLine;

            PointGridSettingsBorder.IsEnabled = meshTool;
            PointGridSettingsPanel.IsEnabled = meshTool;
            CellSizeBox.IsEnabled = meshTool;
            RotationSlider.IsEnabled = meshTool;

            MeshDensityPanel.IsEnabled = meshTool;
            MeshDensityBox.IsEnabled = meshTool;
            ModifyBoundaryCheck.IsEnabled = meshTool;

            ShowPreviewCheck.IsEnabled = shapeTool || tool == ModifyTopoTool.InflateSurface;
            PickApplyBtn.Visibility = shapeTool ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            PickLinesBtn.Visibility = shapeLineTool ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ApplyBtn.Visibility = shapeTool || shapeLineTool ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            if (shapeTool)
            {
                ShowPreviewCheck.IsChecked = true;
                SetPreviewStatus("Di chuột lên topo trong view 3D để preview. Pick để chọn điểm, Ok để ghi.");
                SetDraftStampCount(0);
            }
            else if (shapeLineTool)
            {
                ShowPreviewCheck.IsChecked = false;
                SetPreviewStatus("Bấm Pick Lines để chọn model line / spline. Điểm được thêm theo cao độ line.");
            }

            double inactiveOpacity = 0.45;
            PointGridSettingsBorder.Opacity = meshTool ? 1.0 : inactiveOpacity;
            MeshDensityPanel.Opacity = meshTool ? 1.0 : inactiveOpacity;
        }

        private void PickApply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = BuildSettingsFromUi(validateStrict: true);
                ModifyTopoSettingsService.Instance.Save(settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RequestPickAndApplyStamp?.Invoke(this, EventArgs.Empty);
        }

        private void PickLines_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = BuildSettingsFromUi(validateStrict: true);
                ModifyTopoSettingsService.Instance.Save(settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RequestPickAndApplyLines?.Invoke(this, EventArgs.Empty);
        }

        private void Apply_Click(object sender, RoutedEventArgs e) => ConfirmAndClose(applyOnly: true);

        private void Ok_Click(object sender, RoutedEventArgs e) => ConfirmAndClose(applyOnly: false);

        private void ConfirmAndClose(bool applyOnly)
        {
            try
            {
                var settings = BuildSettingsFromUi(validateStrict: true);
                SelectedOptions = settings.ToOptions(_useMillimeters);
                ModifyTopoSettingsService.Instance.Save(settings);
                IsApplyAction = applyOnly;
                CloseAfterAction = !applyOnly;

                if (applyOnly)
                {
                    SetPreviewStatus("Hover over the toposolid in the 3D view, then click to apply.");
                    return;
                }

                _modelessResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _modelessResult = false;
            Close();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTool == ModifyTopoTool.ShapeByPoint)
            {
                RequestUndoDraftStamp?.Invoke(this, EventArgs.Empty);
                return;
            }

            MessageBox.Show(this, "Undo is available via Revit Undo (Ctrl+Z) after each Apply.",
                "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this, "Redo is available via Revit Redo (Ctrl+Y) after each Apply.",
                "Redo", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private ModifyTopoSettings BuildSettingsFromUi(bool validateStrict = true)
        {
            return new ModifyTopoSettings
            {
                LastTool = _selectedTool,
                CellSizeDisplay = ParseDouble(CellSizeBox.Text, "Cell Size", validateStrict, allowZero: false),
                MeshDensityDisplay = ParseDouble(MeshDensityBox.Text, "Mesh Density", validateStrict, allowZero: false),
                RotationDegrees = RotationSlider.Value,
                ModifyBoundary = ModifyBoundaryCheck.IsChecked == true,
                ShowPreview = ShowPreviewCheck.IsChecked == true,
                InflateRadiusDisplay = ParseDouble(InflateRadiusBox.Text, "Inflate Radius", validateStrict),
                InflateHeightDisplay = ParseDouble(InflateHeightBox.Text, "Inflate Height", validateStrict),
                InflateFalloff = GetComboEnum<SculptFalloffType>(InflateFalloffCombo),
                ShapeRadiusDisplay = ParseDouble(ShapeRadiusBox.Text, "Shape Size", validateStrict),
                ShapeUseDelta = true,
                ShapeDeltaDisplay = ParseDouble(ShapeDeltaBox.Text, "Gain Value", validateStrict, allowNegative: true),
                ShapePointDensity = (int)Math.Round(ShapePointDensitySlider.Value),
                ShapeFalloff = GetComboEnum<SculptFalloffType>(ShapeFalloffCombo),
                SmoothAlgorithm = GetComboEnum<SmoothAlgorithm>(SmoothAlgoCombo),
                SmoothIterations = ParseInt(SmoothIterationsBox.Text, "Smooth iterations", validateStrict),
                SmoothStrength = SmoothStrengthSlider.Value,
                CurvatureThreshold = ParseDouble(CurvatureThresholdBox.Text, "Curvature threshold", validateStrict, allowZero: true),
                RemeshEntireSurface = RemeshEntireCheck.IsChecked == true,
                LineSampleMode = LineCountModeOption.IsChecked == true
                    ? BoundarySampleMode.BySegmentCount
                    : BoundarySampleMode.ByDistance,
                LineUseCustomSpacing = LineDistCustomOption.IsChecked == true,
                LineDistancePresetIndex = LineDistCustomOption.IsChecked == true ? -1
                    : LineDistPreset2.IsChecked == true ? 1
                    : LineDistPreset3.IsChecked == true ? 2 : 0,
                LineCustomSpacingDisplay = ParseLineCustomSpacing(validateStrict),
                LineUseCustomSegmentCount = LineCountCustomOption.IsChecked == true,
                LineSegmentPresetIndex = LineCountCustomOption.IsChecked == true ? -1
                    : LineCountPreset2.IsChecked == true ? 1
                    : LineCountPreset3.IsChecked == true ? 2 : 0,
                LineCustomSegmentCount = ParseLineCustomSegmentCount(validateStrict)
            };
        }

        private double ParseLineCustomSpacing(bool validateStrict)
        {
            string raw = (LineDistCustomTextBox.Text ?? string.Empty).Trim().Replace(",", ".");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
            {
                if (!validateStrict || LineDistCustomOption.IsChecked != true)
                    return _useMillimeters ? 300 : 1;
                throw new InvalidOperationException(
                    $"Please enter a positive custom spacing in {(_useMillimeters ? "mm" : "feet")}.");
            }

            return value;
        }

        private int ParseLineCustomSegmentCount(bool validateStrict)
        {
            string raw = (LineCountCustomTextBox.Text ?? string.Empty).Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            {
                if (!validateStrict || LineCountCustomOption.IsChecked != true)
                    return 10;
                throw new InvalidOperationException("Please enter a whole number of segments (1 or greater).");
            }

            return value;
        }

        private double ParseDouble(string text, string fieldName, bool validateStrict, bool allowZero = false, bool allowNegative = false)
        {
            string raw = (text ?? string.Empty).Trim().Replace(",", ".");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                if (!validateStrict) return 0;
                throw new InvalidOperationException($"Please enter a valid number for {fieldName}.");
            }
            if (validateStrict && !allowZero && !allowNegative && value <= 0)
                throw new InvalidOperationException($"{fieldName} must be greater than zero.");
            if (validateStrict && !allowNegative && value < 0)
                throw new InvalidOperationException($"{fieldName} cannot be negative.");
            return value;
        }

        private int ParseInt(string text, string fieldName, bool validateStrict)
        {
            string raw = (text ?? string.Empty).Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            {
                if (!validateStrict) return 1;
                throw new InvalidOperationException($"{fieldName} must be a whole number ≥ 1.");
            }
            return value;
        }

        private static T GetComboEnum<T>(ComboBox combo) where T : struct, Enum
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is T value)
                return value;
            return default;
        }
    }
}
