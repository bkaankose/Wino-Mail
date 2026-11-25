using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Shown for Settings pages that have no Linux view yet, and when a page fails to open, so the window keeps its sidebar,
/// history and title. It holds no handler and no ViewModel, so it is not tracked for leaks.
/// </summary>
internal sealed class SettingsPlaceholderView : IWinoView
{
    private readonly Adw.StatusPage _root = Adw.StatusPage.New();

    public SettingsPlaceholderView() : this(WinoPage.None, null) { }

    public SettingsPlaceholderView(WinoPage page, string? message = null)
    {
        Page = page;
        _root.SetTitle(page == WinoPage.None ? string.Empty : SettingsCatalog.Title(page));
        _root.SetDescription(message ?? Translator.ComingSoon);
        _root.SetChild(new WinoIcon(SettingsCatalog.Glyph(page), 48).Root);
        _root.SetVexpand(true);
    }

    public WinoPage Page { get; }
    public Gtk.Widget Root => _root;
    public object AssociatedViewModel => this;
    public bool HasPendingWork => false;
    public Task ActivateAsync(NavigationMode mode, object? parameter) => Task.CompletedTask;
    public Task ReleaseAsync() => Task.CompletedTask;
}
