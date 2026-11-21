using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// One mark of the address step's "Works with" shelf (Windows AccountSetupProviderShelfItem): a round brand glyph, or
/// a capsule with a label for IMAP and POP3. The matched provider gets an accent ring and the others dim. It is a
/// picture, not a button.
/// </summary>
internal sealed class AccountSetupShelfItem
{
    private readonly Gtk.Box _root;
    private readonly WinoIcon _glyph = new(WinoIconGlyph.None, 18, WinoIconMode.Colorful);
    private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);
    private AccountSetupProviderShelfItem? _item;

    public AccountSetupShelfItem()
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _root.AddCssClass("wino-setup-shelf-item");
        _root.AccessibleRole = Gtk.AccessibleRole.Img;
        _root.SetValign(Gtk.Align.Center);
        _glyph.Root.SetHexpand(true);
        _label.AddCssClass("caption-heading");
        _label.SetHexpand(true);
        _root.Append(_glyph.Root);
        _root.Append(_label);
    }

    public Gtk.Widget Root => _root;

    public void Apply(AccountSetupProviderShelfItem item)
    {
        if (_item == item) return;
        _item = item;
        _glyph.SetIcon(item.HasGlyph ? item.Glyph : WinoIconGlyph.None);
        _glyph.Root.SetVisible(item.HasGlyph);
        _label.SetText(item.Label);
        _label.SetVisible(item.HasLabel);
        if (item.HasLabel) _root.AddCssClass("wino-setup-shelf-label");
        else _root.RemoveCssClass("wino-setup-shelf-label");
        if (item.IsMatched) _root.AddCssClass("wino-setup-matched");
        else _root.RemoveCssClass("wino-setup-matched");
        _root.SetOpacity(item.IsDimmed ? 0.4 : 1);
        _root.SetTooltipText(item.Title);
        WinoA11y.Label(_root, item.Title);
    }
}

/// <summary>One entry of the provider picker (Windows AccountSetupProviderChoice): a flat button spanning the row, so it can be clicked by pointer, keyboard and AT-SPI.</summary>
internal static class AccountSetupProviderChoiceRow
{
    public static Gtk.Button Create(AccountSetupProviderChoice choice, bool isCurrent)
    {
        var icon = new WinoIcon(choice.Glyph, 16, WinoIconMode.Colorful);
        var title = Gtk.Label.New(choice.Title);
        title.SetXalign(0);
        title.SetHexpand(true);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.Append(icon.Root);
        box.Append(title);
        if (choice.HasDescription)
        {
            var description = Gtk.Label.New(choice.Description);
            description.AddCssClass("caption");
            description.AddCssClass("dim-label");
            box.Append(description);
        }
        if (isCurrent) box.Append(WinoIcons.Label(WinoIconGlyph.Checkmark, 14));

        var button = Gtk.Button.New();
        button.SetChild(box);
        button.AddCssClass("flat");
        button.AddCssClass("wino-setup-provider-choice");
        WinoA11y.Rename(button, choice.AutomationName);
        return button;
    }
}
