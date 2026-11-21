namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Sections that are not built yet.</summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Gtk.Widget BuildOAuthChoice() => Empty();
    private Gtk.Widget BuildOAuthSignedIn() => Empty();
    private Gtk.Widget BuildOrganizationSignIn() => Empty();
    private Gtk.Widget BuildEditFeatures() => Empty();
    private Gtk.Widget BuildEditSignIn() => Empty();
    private Adw.PreferencesRow BuildCalDav() => Hidden(Adw.PreferencesRow.New());
    private Adw.PreferencesRow BuildCardDav() => Hidden(Adw.PreferencesRow.New());
    private Gtk.Widget BuildPop3ServerCopies() => Empty();
    private Gtk.Widget BuildAdvanced() => Empty();

    private static Gtk.Widget Empty() => Hidden(Gtk.Box.New(Gtk.Orientation.Vertical, 0));

    private static T Hidden<T>(T widget) where T : Gtk.Widget
    {
        widget.SetVisible(false);
        return widget;
    }
}
