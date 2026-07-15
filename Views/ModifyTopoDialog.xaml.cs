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
            WireLivePreviewEvents();

            ShapeDeltaMode.Checked += (_, __) => { SetShapeElevationMode(true); RaiseLiveOptionsChanged(); };
            ShapeAbsoluteMode.Checked += (_, __) => { SetShapeElevationMode(false); RaiseLiveOptionsChanged(); };
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
        }

        /// <summary>Modeless show so Revit view stays interactive for hover preview.</summary>
        public bool? ShowModelessAndWait()
        {
            var frame = new DispatcherFrame();
            bool? result = null;
            Closed += (_, __) =>
            {
                result = DialogResult;
                frame.Continue = false;
            };
            Show();
            Dispatcher.PushFrame(frame);
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
            Hook(ShapeTargetBox);
            Hook(ShowPreviewCheck);
            Hook(ShapeFalloffCombo);
            SmoothStrengthSlider.ValueChanged += (_, __) => RaiseLiveOptionsChanged();
        }

        private void RaiseLiveOptionsChanged() => LiveOptionsChanged?.Invoke(this, EventArgs.Empty);

        private void SetupUnits()
        {
            string u = _lengthUnit;
            InflateRadiusUnit.Text = u;
            InflateHeightUnit.Text = u;
            ShapeRadiusUnit.Text = u;
            ShapeDeltaUnit.Text = u;
            ShapeTargetUnit.Text = u;
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
                ShapeDeltaBox.Text = "1.00";
            }
            else
            {
                CellSizeBox.Text = "3.28";
                MeshDensityBox.Text = "24.61";
                InflateRadiusBox.Text = "32.81";
                InflateHeightBox.Text = "3.28";
                ShapeRadiusBox.Text = "32.81";
                ShapeDeltaBox.Text = "3.28";
            }
            ShowPreviewCheck.IsChecked = true;
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
            ShapeTargetBox.Text = settings.ShapeTargetElevationDisplay.ToString(CultureInfo.InvariantCulture);
            ShapeDeltaBox.Text = settings.ShapeDeltaDisplay.ToString(CultureInfo.InvariantCulture);
            if (settings.ShapeUseDelta)
                ShapeDeltaMode.IsChecked = true;
            else
                ShapeAbsoluteMode.IsChecked = true;
            SelectComboByTag(ShapeFalloffCombo, settings.ShapeFalloff);

            SelectComboByTag(SmoothAlgoCombo, settings.SmoothAlgorithm);
            SmoothIterationsBox.Text = settings.SmoothIterations.ToString(CultureInfo.InvariantCulture);
            SmoothStrengthSlider.Value = settings.SmoothStrength;
            CurvatureThresholdBox.Text = settings.CurvatureThreshold.ToString(CultureInfo.InvariantCulture);
            RemeshEntireCheck.IsChecked = settings.RemeshEntireSurface;

            SelectTool(settings.LastTool);
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

        private void SetShapeElevationMode(bool useDelta)
        {
            ShapeDeltaBox.IsEnabled = useDelta;
            ShapeTargetBox.IsEnabled = !useDelta;
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
            SmoothToolBtn.IsChecked = tool == ModifyTopoTool.SmoothGeometry;

            InflatePanel.Visibility = tool == ModifyTopoTool.InflateSurface ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            MeshPanel.Visibility = tool == ModifyTopoTool.MeshControl ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ShapePanel.Visibility = tool == ModifyTopoTool.ShapeByPoint ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SmoothPanel.Visibility = tool == ModifyTopoTool.SmoothGeometry ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            UpdateToolDependentControls(tool);
            RaiseLiveOptionsChanged();
        }

        private void UpdateToolDependentControls(ModifyTopoTool tool)
        {
            bool meshTool = tool == ModifyTopoTool.MeshControl;
            bool shapeTool = tool == ModifyTopoTool.ShapeByPoint;
            bool gridSettingsUsed = meshTool || shapeTool;

            PointGridSettingsBorder.IsEnabled = gridSettingsUsed;
            PointGridSettingsPanel.IsEnabled = gridSettingsUsed;
            CellSizeBox.IsEnabled = gridSettingsUsed;
            RotationSlider.IsEnabled = gridSettingsUsed;

            MeshDensityPanel.IsEnabled = meshTool;
            MeshDensityBox.IsEnabled = meshTool;
            ModifyBoundaryCheck.IsEnabled = meshTool;

            bool previewTool = shapeTool || tool == ModifyTopoTool.InflateSurface;
            ShowPreviewCheck.IsEnabled = previewTool;
            if (!previewTool)
                ShowPreviewCheck.IsChecked = false;

            double inactiveOpacity = 0.45;
            PointGridSettingsBorder.Opacity = gridSettingsUsed ? 1.0 : inactiveOpacity;
            MeshDensityPanel.Opacity = meshTool ? 1.0 : inactiveOpacity;
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
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
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
                ShapeRadiusDisplay = ParseDouble(ShapeRadiusBox.Text, "Shape Radius", validateStrict),
                ShapeUseDelta = ShapeDeltaMode.IsChecked == true,
                ShapeDeltaDisplay = ParseDouble(ShapeDeltaBox.Text, "Delta elevation", validateStrict),
                ShapeTargetElevationDisplay = ParseDouble(ShapeTargetBox.Text, "Target elevation", validateStrict, allowZero: true),
                ShapeFalloff = GetComboEnum<SculptFalloffType>(ShapeFalloffCombo),
                SmoothAlgorithm = GetComboEnum<SmoothAlgorithm>(SmoothAlgoCombo),
                SmoothIterations = ParseInt(SmoothIterationsBox.Text, "Smooth iterations", validateStrict),
                SmoothStrength = SmoothStrengthSlider.Value,
                CurvatureThreshold = ParseDouble(CurvatureThresholdBox.Text, "Curvature threshold", validateStrict, allowZero: true),
                RemeshEntireSurface = RemeshEntireCheck.IsChecked == true
            };
        }

        private double ParseDouble(string text, string fieldName, bool validateStrict, bool allowZero = false)
        {
            string raw = (text ?? string.Empty).Trim().Replace(",", ".");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                if (!validateStrict) return 0;
                throw new InvalidOperationException($"Please enter a valid number for {fieldName}.");
            }
            if (validateStrict && !allowZero && value <= 0)
                throw new InvalidOperationException($"{fieldName} must be greater than zero.");
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
