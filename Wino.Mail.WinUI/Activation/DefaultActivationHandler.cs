using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wino.Views;

namespace Wino.Activation;

internal class DefaultActivationHandler : ActivationHandler<IActivatedEventArgs>
{
    protected override Task HandleInternalAsync(IActivatedEventArgs args)
    {
        (Window.Current.Content as Frame).Navigate(typeof(AppShell), null, new DrillInNavigationTransitionInfo());

        return Task.CompletedTask;
    }

    // Only navigate if Frame content doesn't exist.
    protected override bool CanHandleInternal(IActivatedEventArgs args)
        => (Window.Current?.Content as Frame)?.Content == null;
}
