using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;

namespace Wino.MenuFlyouts
{
    public class WinoOperationFlyout<TActionType> : MenuFlyout, IDisposable where TActionType : class
    {
        public TActionType ClickedOperation { get; set; }

        protected readonly IEnumerable<TActionType> AvailableActions;

        private readonly TaskCompletionSource<TActionType> _completionSource;

        public WinoOperationFlyout(IEnumerable<TActionType> availableActions, TaskCompletionSource<TActionType> completionSource)
        {
            _completionSource = completionSource;

            AvailableActions = availableActions;

            Closing += FlyoutClosing;
        }

        private void FlyoutClosing(Windows.UI.Xaml.Controls.Primitives.FlyoutBase sender, Windows.UI.Xaml.Controls.Primitives.FlyoutBaseClosingEventArgs args)
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
            foreach (var item in Items)
            {
                if (item is IDisposable disposableItem)
                {
                    disposableItem.Dispose();
                }
            }
        }
    }
}
