using Microsoft.UI.Xaml;
using Wino.Views;

namespace Wino
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public void StartWino()
        {
            WindowFrame.Navigate(typeof(AppShell));
        }
    }
}
