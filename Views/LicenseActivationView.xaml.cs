using System.Windows;
using effetopo.Services;
using effetopo.ViewModels;

namespace effetopo.Views
{
    /// <summary>
    /// Interaction logic for LicenseActivationView.xaml
    /// </summary>
    public partial class LicenseActivationView : Window
    {
        public LicenseActivationViewModel ViewModel { get; }

        public LicenseActivationView()
        {
            InitializeComponent();
            ViewModel = new LicenseActivationViewModel(this);
            DataContext = ViewModel;
        }
    }
}