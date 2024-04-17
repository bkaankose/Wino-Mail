using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs
{
    public sealed partial class ConfirmationDialog : ContentDialog, IConfirmationDialog
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

        public string ApproveButtonTitle
        {
            get { return (string)GetValue(ApproveButtonTitleProperty); }
            set { SetValue(ApproveButtonTitleProperty, value); }
        }

        public static readonly DependencyProperty ApproveButtonTitleProperty = DependencyProperty.Register(nameof(ApproveButtonTitle), typeof(string), typeof(ConfirmationDialog), new PropertyMetadata(string.Empty));

        #endregion

        private bool _isApproved;
        public ConfirmationDialog()
        {
            InitializeComponent();
        }

        public async Task<bool> ShowDialogAsync(string title, string message, string approveButtonTitle)
        {
            _completionSource = new TaskCompletionSource<bool>();

            DialogTitle = title;
            Message = message;
            ApproveButtonTitle = approveButtonTitle;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            ShowAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

            return await _completionSource.Task;
        }

        private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _completionSource.TrySetResult(_isApproved);
        }

        private void ApproveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            _isApproved = true;

            Hide();
        }

        private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            _isApproved = false;

            Hide();
        }
    }
}
