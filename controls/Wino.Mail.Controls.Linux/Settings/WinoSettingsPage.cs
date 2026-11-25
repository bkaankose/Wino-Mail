namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// The shell of a settings page: an <c>Adw.PreferencesPage</c> (it scrolls and clamps itself) inside a toolbar view
/// that carries page banners on top and an optional bottom bar. The Settings window owns the header bar.
/// </summary>
public sealed class WinoSettingsPage
{
    private readonly Adw.ToolbarView _toolbar;
    private Gtk.Widget? _bottomBar;

    public WinoSettingsPage()
    {
        _toolbar = Adw.ToolbarView.New();
        Page = Adw.PreferencesPage.New();
        _toolbar.SetContent(Page);
        _toolbar.SetVexpand(true);
        _toolbar.SetHexpand(true);
    }

    public Gtk.Widget Root => _toolbar;

    public Adw.PreferencesPage Page { get; }

    public WinoSettingsGroup AddGroup(string? title = null, string? description = null, params Gtk.Widget?[] rows)
    {
        var group = new WinoSettingsGroup(title, description);
        group.AddRange(rows);
        return Add(group);
    }

    public WinoSettingsGroup Add(WinoSettingsGroup group)
    {
        Page.Add(group.Root);
        return group;
    }

    public void Remove(WinoSettingsGroup group) => Page.Remove(group.Root);

    /// <summary>A page banner, hidden until <c>SetRevealed(true)</c>; banners stack in call order.</summary>
    public Adw.Banner AddBanner(string? title = null)
    {
        var banner = Adw.Banner.New(string.Empty);
        banner.SetUseMarkup(false);
        banner.SetTitle(title ?? string.Empty);
        banner.SetRevealed(false);
        _toolbar.AddTopBar(banner);
        return banner;
    }

    public void SetBottomBar(Gtk.Widget? bar)
    {
        if (ReferenceEquals(_bottomBar, bar)) return;
        if (_bottomBar is not null) _toolbar.Remove(_bottomBar);
        _bottomBar = bar;
        if (bar is not null) _toolbar.AddBottomBar(bar);
    }
}
