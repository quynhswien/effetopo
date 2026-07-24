using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Views
{
    public partial class SetElevationDialog : Window
    {
        private readonly bool _useMillimeters;
        private readonly Document _doc;
        private byte _colorR = 255;
        private byte _colorG = 128;
        private byte _colorB = 0;

        public SetElevationOptions? SelectedOptions { get; private set; }

        public SetElevationMode SelectedMode { get; private set; } = SetElevationMode.Set;

        private static readonly IReadOnlyList<ColorOption> ColorOptions = new[]
        {
            new ColorOption("Orange", 255, 128, 0),
            new ColorOption("Red", 255, 0, 0),
            new ColorOption("Green", 0, 176, 80),
            new ColorOption("Blue", 0, 112, 192),
            new ColorOption("Cyan", 0, 176, 240),
            new ColorOption("Magenta", 192, 0, 192),
            new ColorOption("Yellow", 255, 192, 0)
        };

        public SetElevationDialog(Document doc, bool useMillimeters, SetElevationSettings? initialSettings = null)
        {
            InitializeComponent();
            _doc = doc;
            _useMillimeters = useMillimeters;

            string unitLabel = _useMillimeters ? "mm" : "ft";
            LengthUnitText.Text = unitLabel;
            IncrementUnitText.Text = unitLabel;

            PopulateElevationBaseCombo();
            PopulateTextTypes();
            PopulateColorCombo();

            AddLabelCheckBox.Checked += AddLabelCheckBox_Changed;
            AddLabelCheckBox.Unchecked += AddLabelCheckBox_Changed;
            OverrideColorCombo.SelectionChanged += OverrideColorCombo_SelectionChanged;

            if (initialSettings != null)
                ApplySettings(initialSettings);
            else
                AddLabelCheckBox.IsChecked = true;

            UpdateTextTypeEnabled();
        }

        private void PopulateColorCombo()
        {
            OverrideColorCombo.ItemsSource = ColorOptions;
            OverrideColorCombo.SelectedIndex = 0;
        }

        private void OverrideColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (OverrideColorCombo.SelectedItem is ColorOption option)
            {
                _colorR = option.R;
                _colorG = option.G;
                _colorB = option.B;
            }
        }

        private void PopulateElevationBaseCombo()
        {
            ElevationBaseCombo.ItemsSource = Enum.GetValues(typeof(ElevationBaseType))
                .Cast<ElevationBaseType>()
                .Select(e => new ComboBoxItem
                {
                    Content = FormatElevationBaseName(e),
                    Tag = e
                })
                .ToArray();

            SelectElevationBase(ElevationBaseType.SurveyPoint);
        }

        private void PopulateTextTypes()
        {
            var types = new FilteredElementCollector(_doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .OrderBy(t => t.Name)
                .Select(t => new TextTypeItem
                {
                    Id = GetElementIdValue(t.Id),
                    Name = t.Name
                })
                .ToList();

            TextTypeCombo.ItemsSource = types;
            if (types.Count > 0)
                TextTypeCombo.SelectedIndex = 0;
        }

        private void ApplySettings(SetElevationSettings settings)
        {
            AddLabelCheckBox.IsChecked = settings.AddLabel;
            StartElevationTextBox.Text = settings.StartElevationDisplay.ToString(CultureInfo.InvariantCulture);
            IncrementTextBox.Text = settings.IncrementDisplay.ToString(CultureInfo.InvariantCulture);
            SelectElevationBase(settings.ElevationBase);

            _colorR = settings.OverrideColorR;
            _colorG = settings.OverrideColorG;
            _colorB = settings.OverrideColorB;
            SelectColor(settings.OverrideColorR, settings.OverrideColorG, settings.OverrideColorB);

            if (settings.TextTypeId > 0)
            {
                foreach (TextTypeItem item in TextTypeCombo.Items)
                {
                    if (item.Id == settings.TextTypeId)
                    {
                        TextTypeCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void SelectElevationBase(ElevationBaseType baseType)
        {
            foreach (ComboBoxItem item in ElevationBaseCombo.Items)
            {
                if (item.Tag is ElevationBaseType value && value == baseType)
                {
                    ElevationBaseCombo.SelectedItem = item;
                    return;
                }
            }

            ElevationBaseCombo.SelectedIndex = 0;
        }

        private static string FormatElevationBaseName(ElevationBaseType baseType) => baseType switch
        {
            ElevationBaseType.TopPlane => "Top Plane",
            ElevationBaseType.CurrentLevel => "Current Level",
            ElevationBaseType.ProjectBasePoint => "Project Base Point",
            ElevationBaseType.SurveyPoint => "Survey Point",
            ElevationBaseType.InternalOrigin => "Internal Origin",
            _ => baseType.ToString()
        };

        private void SelectColor(byte r, byte g, byte b)
        {
            foreach (ColorOption option in ColorOptions)
            {
                if (option.R == r && option.G == g && option.B == b)
                {
                    OverrideColorCombo.SelectedItem = option;
                    return;
                }
            }

            OverrideColorCombo.SelectedIndex = 0;
        }

        private void AddLabelCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateTextTypeEnabled();

        private void UpdateTextTypeEnabled()
        {
            if (TextTypeCombo == null || AddLabelCheckBox == null) return;
            TextTypeCombo.IsEnabled = AddLabelCheckBox.IsChecked == true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) =>
            ConfirmAndClose(SetElevationMode.Set);

        private void MatchElevation_Click(object sender, RoutedEventArgs e) =>
            ConfirmAndClose(SetElevationMode.Match);

        private void ConfirmAndClose(SetElevationMode mode)
        {
            try
            {
                var settings = BuildSettingsFromUi();
                SelectedOptions = settings.ToOptions(_useMillimeters);
                SelectedMode = mode;
                Services.SetElevationSettingsService.Instance.Save(settings);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private SetElevationSettings BuildSettingsFromUi()
        {
            if (!TryParsePositiveOrZero(StartElevationTextBox.Text, out double startElevation))
            {
                throw new InvalidOperationException($"Please enter a valid Start Elevation in {(_useMillimeters ? "mm" : "feet")}.");
            }

            if (!TryParsePositiveOrZero(IncrementTextBox.Text, out double increment))
            {
                throw new InvalidOperationException($"Please enter a valid Increment in {(_useMillimeters ? "mm" : "feet")}.");
            }

            if (AddLabelCheckBox.IsChecked == true && TextTypeCombo.SelectedItem == null)
            {
                throw new InvalidOperationException("Please select a Text Type when Add Label is enabled.");
            }

            var selectedBase = ElevationBaseCombo.SelectedItem as ComboBoxItem;
            var elevationBase = selectedBase?.Tag is ElevationBaseType value
                ? value
                : ElevationBaseType.SurveyPoint;

            long textTypeId = 0;
            if (TextTypeCombo.SelectedItem is TextTypeItem textType)
                textTypeId = textType.Id;

            return new SetElevationSettings
            {
                AddLabel = AddLabelCheckBox.IsChecked == true,
                TextTypeId = textTypeId,
                StartElevationDisplay = startElevation,
                IncrementDisplay = increment,
                ElevationBase = elevationBase,
                OverrideColorR = _colorR,
                OverrideColorG = _colorG,
                OverrideColorB = _colorB
            };
        }

        private static bool TryParsePositiveOrZero(string? raw, out double value)
        {
            raw = (raw ?? string.Empty).Trim().Replace(",", ".");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            return true;
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

        private sealed class TextTypeItem
        {
            public long Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        private sealed class ColorOption
        {
            public ColorOption(string name, byte r, byte g, byte b)
            {
                Name = name;
                R = r;
                G = g;
                B = b;
            }

            public string Name { get; }
            public byte R { get; }
            public byte G { get; }
            public byte B { get; }

            public SolidColorBrush Brush =>
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(R, G, B));
        }
    }
}
