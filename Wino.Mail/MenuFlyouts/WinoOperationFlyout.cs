using System;
using System.Collections.Generic;
using System.Threading.Tasks;

#if NET8_0
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
#else
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
#endif
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

        private void FlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
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
