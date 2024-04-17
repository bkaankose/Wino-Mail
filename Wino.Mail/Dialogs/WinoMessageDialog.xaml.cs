using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Wino.Dialogs
{
    public sealed partial class WinoMessageDialog : ContentDialog
    {
        private TaskCompletionSource<bool> _completionSource;

        #region Dependency Properties

        public string DialogTitle
        {
            get { return (string)GetValue(DialogTitleProperty); }
            set { SetValue(DialogTitleProperty, value); }
        }

        public static readonly DependencyProperty DialogTitleProperty = DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(ConfirmationDialog), new PropertyMetadata(string.Empty));

        public string Message
        {
            get { return (string)GetValue(MessageProperty); }
            set { SetValue(MessageProperty, value); }
        }

        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(ConfirmationDialog), new PropertyMetadata(string.Empty));

        #endregion

        public WinoMessageDialog()
        {
            InitializeComponent();
        }

        public async Task<bool> ShowDialogAsync(string title, string message)
        {
            _completionSource = new TaskCompletionSource<bool>();

            DialogTitle = title;
            Message = message;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            ShowAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

            return await _completionSource.Task;
        }

        private void ApproveClicked(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _completionSource.TrySetResult(true);
        }
    }
}
