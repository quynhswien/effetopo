using effetopo.ViewModels;

namespace effetopo.Views
{
    public sealed partial class effetopoView
    {
        public effetopoView(effetopoViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
        }
    }
}