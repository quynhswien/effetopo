using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using effetopo.Models;
using effetopo.Services;

namespace effetopo.Views
{
    public partial class CreateContourLineDialog : Window
    {
        private readonly bool _useMillimeters;
        private readonly Document _doc;

        public CreateContourLineOptions? SelectedOptions { get; private set; }

        public CreateContourLineDialog(Document doc, bool useMillimeters, CreateContourLineSettings? initialSettings = null)
        {
            InitializeComponent();
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _useMillimeters = useMillimeters;

            string unitLabel = _useMillimeters ? "mm" : "ft";
            IntervalUnitText.Text = unitLabel;
            MajorIntervalUnitText.Text = unitLabel;
            IntervalTextBox.Text = (_useMillimeters ? 300 : 1).ToString(CultureInfo.InvariantCulture);
            MajorIntervalTextBox.Text = (_useMillimeters ? 1500 : 5).ToString(CultureInfo.InvariantCulture);

            PopulateLineStyles();
            PopulateLevels();

            UseMajorMinorCheckBox.Checked += MajorMinorSettings_Changed;
            UseMajorMinorCheckBox.Unchecked += MajorMinorSettings_Changed;
            AssignLevelCheckBox.Checked += AssignLevelSettings_Changed;
            AssignLevelCheckBox.Unchecked += AssignLevelSettings_Changed;

            if (initialSettings != null)
                ApplySettings(initialSettings);
            else
            {
                UseMajorMinorCheckBox.IsChecked = true;
                AssignLevelCheckBox.IsChecked = true;
            }

            UpdateMajorMinorEnabled();
            UpdateLevelEnabled();
        }

        public void ApplySuggestedInterval(double intervalFeet)
        {
            if (intervalFeet <= 0) return;
            double display = _useMillimeters ? intervalFeet * 304.8 : intervalFeet;
            IntervalTextBox.Text = display.ToString(CultureInfo.InvariantCulture);
        }

        public void ApplySuggestedMajorInterval(double majorIntervalFeet)
        {
            if (majorIntervalFeet <= 0) return;
            double display = _useMillimeters ? majorIntervalFeet * 304.8 : majorIntervalFeet;
            MajorIntervalTextBox.Text = display.ToString(CultureInfo.InvariantCulture);
        }

        public void ApplySuggestedLevel(long levelId)
        {
            if (levelId <= 0) return;
            SelectLevel(levelId);
        }

        private void PopulateLineStyles()
        {
#if REVIT2024_OR_GREATER
            IReadOnlyList<string> names = CreateContourLineRevitHelper.GetLineStyleNames(_doc);
            var items = names
                .Select(name => new LineStyleItem
                {
                    Name = name,
                    DisplayName = CreateContourLineRevitHelper.FormatLineStyleDisplayName(name)
                })
                .ToList();

            MinorLineStyleCombo.ItemsSource = items;
            MajorLineStyleCombo.ItemsSource = items;

            SelectLineStyle(MinorLineStyleCombo, string.Empty);
            SelectLineStyle(MajorLineStyleCombo, "Wide Lines");
#endif
        }

        private void PopulateLevels()
        {
#if REVIT2024_OR_GREATER
            var levels = CreateContourLineRevitHelper.GetSortedLevels(_doc)
                .Select(level => new LevelItem
                {
                    Id = GetElementIdValue(level.Id),
                    Name = level.Name
                })
                .ToList();

            LevelCombo.ItemsSource = levels;
            if (levels.Count > 0)
                LevelCombo.SelectedIndex = 0;
#endif
        }

        private void ApplySettings(CreateContourLineSettings settings)
        {
            IntervalTextBox.Text = settings.IntervalDisplay.ToString(CultureInfo.InvariantCulture);
            MajorIntervalTextBox.Text = settings.MajorIntervalDisplay.ToString(CultureInfo.InvariantCulture);
            UseMajorMinorCheckBox.IsChecked = settings.UseMajorMinorContours;
            AssignLevelCheckBox.IsChecked = settings.AssignLevel;
            SelectLineStyle(MinorLineStyleCombo, settings.MinorLineStyleName);
            SelectLineStyle(MajorLineStyleCombo, settings.MajorLineStyleName);
            SelectLevel(settings.LevelId);
        }

        private void SelectLineStyle(ComboBox combo, string? lineStyleName)
        {
            lineStyleName ??= string.Empty;
            foreach (LineStyleItem item in combo.Items)
            {
                if (string.Equals(item.Name, lineStyleName, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private void SelectLevel(long levelId)
        {
            if (levelId <= 0)
            {
                if (LevelCombo.Items.Count > 0)
                    LevelCombo.SelectedIndex = 0;
                return;
            }

            foreach (LevelItem item in LevelCombo.Items)
            {
                if (item.Id == levelId)
                {
                    LevelCombo.SelectedItem = item;
                    return;
                }
            }
        }

        private void MajorMinorSettings_Changed(object sender, RoutedEventArgs e) => UpdateMajorMinorEnabled();

        private void AssignLevelSettings_Changed(object sender, RoutedEventArgs e) => UpdateLevelEnabled();

        private void UpdateMajorMinorEnabled()
        {
            if (MajorIntervalPanel == null || MajorLineStylePanel == null || UseMajorMinorCheckBox == null)
                return;

            bool enabled = UseMajorMinorCheckBox.IsChecked == true;
            MajorIntervalPanel.IsEnabled = enabled;
            MajorLineStylePanel.IsEnabled = enabled;
        }

        private void UpdateLevelEnabled()
        {
            if (LevelPanel == null || AssignLevelCheckBox == null)
                return;

            LevelPanel.IsEnabled = AssignLevelCheckBox.IsChecked == true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = BuildSettingsFromUi();
                SelectedOptions = settings.ToOptions(_useMillimeters);
                CreateContourLineSettingsService.Instance.Save(settings);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private CreateContourLineSettings BuildSettingsFromUi()
        {
            if (!TryParsePositive(IntervalTextBox.Text, out double interval))
            {
                throw new InvalidOperationException(
                    $"Please enter a positive contour interval in {(_useMillimeters ? "mm" : "feet")}.");
            }

            bool useMajorMinor = UseMajorMinorCheckBox.IsChecked == true;
            double majorInterval = 0;
            if (useMajorMinor)
            {
                if (!TryParsePositive(MajorIntervalTextBox.Text, out majorInterval))
                {
                    throw new InvalidOperationException(
                        $"Please enter a positive major interval in {(_useMillimeters ? "mm" : "feet")}.");
                }
            }

            bool assignLevel = AssignLevelCheckBox.IsChecked == true;
            long levelId = 0;
            if (assignLevel)
            {
                if (LevelCombo.SelectedItem is not LevelItem levelItem)
                    throw new InvalidOperationException("Please select a level.");

                levelId = levelItem.Id;
            }

            string minorLineStyle = MinorLineStyleCombo.SelectedItem is LineStyleItem minorItem
                ? minorItem.Name
                : string.Empty;
            string majorLineStyle = MajorLineStyleCombo.SelectedItem is LineStyleItem majorItem
                ? majorItem.Name
                : string.Empty;

            return new CreateContourLineSettings
            {
                IntervalDisplay = interval,
                UseMajorMinorContours = useMajorMinor,
                MajorIntervalDisplay = majorInterval,
                MinorLineStyleName = minorLineStyle,
                MajorLineStyleName = majorLineStyle,
                AssignLevel = assignLevel,
                LevelId = levelId
            };
        }

        private static bool TryParsePositive(string? raw, out double value)
        {
            raw = (raw ?? string.Empty).Trim().Replace(",", ".");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;

            return value > 0;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value ?? -1;
#else
            return id?.IntegerValue ?? -1;
#endif
        }

        private sealed class LineStyleItem
        {
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
        }

        private sealed class LevelItem
        {
            public long Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }
    }
}
