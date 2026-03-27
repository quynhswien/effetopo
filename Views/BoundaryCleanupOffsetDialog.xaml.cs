using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace effetopo.Views
{
    public partial class BoundaryCleanupOffsetDialog : Window
    {
        private readonly bool _useMillimeters;

        public double? SelectedOffsetFeet { get; private set; }

        public BoundaryCleanupOffsetDialog(bool useMillimeters)
        {
            InitializeComponent();
            _useMillimeters = useMillimeters;

            if (_useMillimeters)
            {
                Preset1.Content = "300 mm (Default)";
                Preset2.Content = "1500 mm";
                Preset3.Content = "3000 mm";
                Preset4.Content = "4500 mm";
                CustomUnitText.Text = "mm";
                CustomValueTextBox.Text = "300";
            }
            else
            {
                Preset1.Content = "1' (Default)";
                Preset2.Content = "5'";
                Preset3.Content = "10'";
                Preset4.Content = "15'";
                CustomUnitText.Text = "feet";
                CustomValueTextBox.Text = "1";
            }

            CustomOption.Checked += (_, __) => CustomValueTextBox.IsEnabled = true;
            Preset1.Checked += DisableCustom;
            Preset2.Checked += DisableCustom;
            Preset3.Checked += DisableCustom;
            Preset4.Checked += DisableCustom;
        }

        private void DisableCustom(object sender, RoutedEventArgs e)
        {
            CustomValueTextBox.IsEnabled = false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CustomOption.IsChecked == true)
                {
                    string raw = (CustomValueTextBox.Text ?? string.Empty).Trim().Replace(",", ".");
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double customValue) || customValue <= 0)
                    {
                        MessageBox.Show(this, $"Please enter a positive numeric value in {( _useMillimeters ? "mm" : "feet")}.",
                            "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    SelectedOffsetFeet = _useMillimeters
                        ? ConvertMillimetersToFeet(customValue)
                        : customValue;
                }
                else if (Preset2.IsChecked == true)
                {
                    SelectedOffsetFeet = _useMillimeters ? ConvertMillimetersToFeet(1500) : 5.0;
                }
                else if (Preset3.IsChecked == true)
                {
                    SelectedOffsetFeet = _useMillimeters ? ConvertMillimetersToFeet(3000) : 10.0;
                }
                else if (Preset4.IsChecked == true)
                {
                    SelectedOffsetFeet = _useMillimeters ? ConvertMillimetersToFeet(4500) : 15.0;
                }
                else
                {
                    SelectedOffsetFeet = _useMillimeters ? ConvertMillimetersToFeet(300) : 1.0;
                }

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

        private static double ConvertMillimetersToFeet(double mm)
        {
            // Revit internal units for length are feet.
            return mm / 304.8;
        }
    }
}

