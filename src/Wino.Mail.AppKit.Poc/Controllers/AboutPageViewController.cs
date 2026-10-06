using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.AppKit.Poc.Infrastructure;
using Wino.Mail.AppKit.Poc.ViewModels;

namespace Wino.Mail.AppKit.Poc.Controllers;

// Controller naming mirrors the existing AboutPage.xaml / AboutPageViewModel pairing.
[Register("AboutPageViewController")]
internal sealed class AboutPageViewController : NSViewController, IDisposable
{
    private readonly AboutPageViewModel _viewModel;
    private readonly List<IDisposable> _bindings = [];
    private bool _active;
    private bool _disposed;

    public AboutPageViewController()
    {
        _viewModel = new AboutPageViewModel(new AppKitDialogService(), new AppKitDispatcher());
    }

    public override void LoadView()
    {
        var root = new NSView(new CGRect(0, 0, 560, 360));
        var heading = new NSTextField(new CGRect(32, 280, 496, 32))
        {
            StringValue = Translator.GetTranslatedString("SettingsAbout_Title"),
            Editable = false,
            Bordered = false,
            DrawsBackground = false,
            Font = NSFont.BoldSystemFontOfSize(22)
        };
        var lifecycleStatus = new NSTextField(new CGRect(32, 220, 496, 28))
        {
            Editable = false,
            Bordered = false,
            DrawsBackground = false
        };
        var pageLoadedStatus = new NSTextField(new CGRect(32, 188, 496, 28))
        {
            Editable = false,
            Bordered = false,
            DrawsBackground = false
        };
        var dialogButton = new NSButton(new CGRect(32, 160, 180, 36))
        {
            Title = "Show dialog"
        };
        var navigationButton = new NSButton(new CGRect(230, 160, 240, 36))
        {
            Title = "Navigate away"
        };
        EventHandler navigationHandler = (_, _) => ToggleNavigation(navigationButton);
        navigationButton.Activated += navigationHandler;

        root.AddSubview(heading);
        root.AddSubview(lifecycleStatus);
        root.AddSubview(pageLoadedStatus);
        root.AddSubview(dialogButton);
        root.AddSubview(navigationButton);
        View = root;

        _bindings.Add(lifecycleStatus.BindText(_viewModel, vm => vm.LifecycleStatus));
        _bindings.Add(pageLoadedStatus.BindText(_viewModel, vm => vm.PageLoadedStatus));
        _bindings.Add(dialogButton.BindCommand(_viewModel.ShowDialogCommand));
        _bindings.Add(new ActionDisposable(() => navigationButton.Activated -= navigationHandler));
    }

    private void ToggleNavigation(NSButton button)
    {
        if (_active)
        {
            Deactivate(NavigationMode.Back);
            button.Title = "Return to About";
        }
        else
        {
            Activate(NavigationMode.Back);
            button.Title = "Navigate away";
        }
    }

    public void Activate(NavigationMode mode = NavigationMode.New, object? parameters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
            return;

        _active = true;
        _viewModel.OnNavigatedTo(mode, parameters!);
        _viewModel.OnPageLoaded();
    }

    public void Deactivate(NavigationMode mode = NavigationMode.Back, object? parameters = null)
    {
        if (!_active)
            return;

        _active = false;
        _viewModel.OnNavigatedFrom(mode, parameters!);
    }

    public new void Dispose()
    {
        if (_disposed)
            return;

        Deactivate();
        _disposed = true;
        foreach (var binding in _bindings)
            binding.Dispose();
        _bindings.Clear();
        _viewModel.Dispose();
    }
}
