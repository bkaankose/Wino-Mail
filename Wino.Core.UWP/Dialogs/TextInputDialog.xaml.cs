using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Wino.Dialogs
{
    public sealed partial class TextInputDialog : ContentDialog
    {
        public bool? HasInput { get; set; }

        public string CurrentInput
        {
            get { return (string)GetValue(CurrentInputProperty); }
            set { SetValue(CurrentInputProperty, value); }
        }

        public static readonly DependencyProperty CurrentInputProperty = DependencyProperty.Register(nameof(CurrentInput), typeof(string), typeof(TextInputDialog), new PropertyMetadata(string.Empty));

        public TextInputDialog()
        {
            InitializeComponent();
        }

        public void SetDescription(string description)
        {
            DialogDescription.Text = description;
        }

        public void SetPrimaryButtonText(string text)
        {
            PrimaryButtonText = text;
        }

        private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Hide();
        }

        private void UpdateOrCreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            HasInput = true;

            Hide();
        }
    }
}
