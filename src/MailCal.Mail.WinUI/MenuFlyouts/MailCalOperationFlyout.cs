using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Mail.Controls.ContextFlyout;

namespace MailCal.MenuFlyouts;

public partial class MailCalOperationFlyout<TActionType> : MailCalContextFlyout, IDisposable where TActionType : class
{
    public TActionType ClickedOperation { get; set; } = null!;

    protected readonly IEnumerable<TActionType> AvailableActions;

    private readonly TaskCompletionSource<TActionType> _completionSource;

    public MailCalOperationFlyout(IEnumerable<TActionType> availableActions, TaskCompletionSource<TActionType> completionSource)
    {
        _completionSource = completionSource;

        AvailableActions = availableActions;

        Closing += FlyoutClosing;
    }

    private void FlyoutClosing(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase sender, Microsoft.UI.Xaml.Controls.Primitives.FlyoutBaseClosingEventArgs args)
    {
        Closing -= FlyoutClosing;

        _completionSource.TrySetResult(ClickedOperation);
    }

    protected void MenuItemClicked(TActionType operation)
    {
        ClickedOperation = operation;

        Hide();
    }

    public void Dispose()
    {
        Closing -= FlyoutClosing;
    }
}
