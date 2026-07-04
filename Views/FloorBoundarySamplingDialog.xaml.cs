using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using effetopo.Models;

namespace effetopo.Views
{
    public partial class FloorBoundarySamplingDialog : Window
    {
        private readonly bool _useMillimeters;

        public FloorBoundarySamplingOptions? SelectedOptions { get; private set; }

        public FloorBoundarySamplingDialog(bool useMillimeters)
        {
            InitializeComponent();
            _useMillimeters = useMillimeters;

            if (_useMillimeters)
            {
                DistPreset1.Content = "300 mm (Default)";
                DistPreset2.Content = "500 mm";
                DistPreset3.Content = "1000 mm";
                DistUnitText.Text = "mm";
                DistCustomTextBox.Text = "300";
            }
            else
            {
                DistPreset1.Content = "1' (Default)";
                DistPreset2.Content = "2'";
                DistPreset3.Content = "4'";
                DistUnitText.Text = "feet";
                DistCustomTextBox.Text = "1";
            }

            CountPreset1.Content = "5 segments (6 points)";
            CountPreset2.Content = "10 segments (11 points)";
            CountPreset3.Content = "20 segments (21 points)";
            CountCustomTextBox.Text = "10";

            DistanceModeOption.Checked += (_, __) => SetModePanels(true);
            CountModeOption.Checked += (_, __) => SetModePanels(false);

            DistCustomOption.Checked += (_, __) => DistCustomTextBox.IsEnabled = true;
            DistPreset1.Checked += (_, __) => DistCustomTextBox.IsEnabled = false;
            DistPreset2.Checked += (_, __) => DistCustomTextBox.IsEnabled = false;
            DistPreset3.Checked += (_, __) => DistCustomTextBox.IsEnabled = false;

            CountCustomOption.Checked += (_, __) => CountCustomTextBox.IsEnabled = true;
            CountPreset1.Checked += (_, __) => CountCustomTextBox.IsEnabled = false;
            CountPreset2.Checked += (_, __) => CountCustomTextBox.IsEnabled = false;
            CountPreset3.Checked += (_, __) => CountCustomTextBox.IsEnabled = false;
        }

        private void SetModePanels(bool distanceMode)
        {
            DistancePanel.IsEnabled = distanceMode;
            CountPanel.IsEnabled = !distanceMode;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var options = new FloorBoundarySamplingOptions();

                if (CountModeOption.IsChecked == true)
                {
                    options.Mode = BoundarySampleMode.BySegmentCount;
                    options.SegmentsPerCurve = ResolveSegmentCount();
                }
                else
                {
                    options.Mode = BoundarySampleMode.ByDistance;
                    options.SpacingFeet = ResolveSpacingFeet();
                }

                SelectedOptions = options;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private double ResolveSpacingFeet()
        {
            if (DistCustomOption.IsChecked == true)
            {
                string raw = (DistCustomTextBox.Text ?? string.Empty).Trim().Replace(",", ".");
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double customValue) || customValue <= 0)
                {
                    MessageBox.Show(this, $"Please enter a positive spacing in {(_useMillimeters ? "mm" : "feet")}.",
                        "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                    throw new InvalidOperationException("Invalid custom spacing.");
                }

                return _useMillimeters ? MillimetersToFeet(customValue) : customValue;
            }

            if (DistPreset2.IsChecked == true)
                return _useMillimeters ? MillimetersToFeet(500) : 2.0;
            if (DistPreset3.IsChecked == true)
                return _useMillimeters ? MillimetersToFeet(1000) : 4.0;
            return _useMillimeters ? MillimetersToFeet(300) : 1.0;
        }

        private int ResolveSegmentCount()
        {
            if (CountCustomOption.IsChecked == true)
            {
                string raw = (CountCustomTextBox.Text ?? string.Empty).Trim();
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1)
                {
                    MessageBox.Show(this, "Please enter a whole number of segments (1 or greater).",
                        "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                    throw new InvalidOperationException("Invalid custom segment count.");
                }
                return count;
            }

            if (CountPreset2.IsChecked == true) return 10;
            if (CountPreset3.IsChecked == true) return 20;
            return 5;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static double MillimetersToFeet(double mm) => mm / 304.8;
    }
}
