using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The header's Wino Account popover (Windows WinoAccountFlyout, macOS WinoAccountPopoverViewController): signed out,
/// a title, two benefit cards, Sign in and Create account; signed in, the picture, name and e-mail with Manage account
/// and Sign out. Every button pops the popover down before its event runs.
/// </summary>
internal sealed class WinoAccountPopover : IDisposable
{
    private const int Width = 320;

    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly WinoContactPicture _picture = new(48);
    private readonly Gtk.Label _name = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _email = Gtk.Label.New(string.Empty);
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly List<Gtk.Button> _buttons = [];
    private readonly List<IDisposable> _subscriptions = [];

    public WinoAccountPopover()
    {
        WinoLiveObjects.Track(this);
        Root = Gtk.Popover.New();
        Root.SetPosition(Gtk.PositionType.Bottom);
        _stack.SetVhomogeneous(false);
        _stack.SetHhomogeneous(true);
        _stack.SetSizeRequest(Width, -1);
        _stack.AddNamed(SignedOut(), "signed-out");
        _stack.AddNamed(SignedIn(), "signed-in");
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.Append(_stack);
        _spinner.SetVisible(false);
        _spinner.SetMarginBottom(8);
        box.Append(_spinner);
        Root.SetChild(box);
    }

    public Gtk.Popover Root { get; }

    public event Action? SignInRequested;
    public event Action? RegisterRequested;
    public event Action? ManageRequested;
    public event Action? SignOutRequested;

    public bool IsSignedIn => _stack.GetVisibleChildName() == "signed-in";

    public void ShowSignedOut() => _stack.SetVisibleChildName("signed-out");

    public void ShowSignedIn(string? displayName, string email, Gdk.Texture? avatar)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
        _name.SetText(name);
        _email.SetText(email);
        _picture.SetIdentity(name, email);
        _picture.Picture = avatar;
        _stack.SetVisibleChildName("signed-in");
    }

    public void SetBusy(bool busy)
    {
        _spinner.SetVisible(busy);
        foreach (var button in _buttons) button.SetSensitive(!busy);
    }

    public void Dispose()
    {
        SignInRequested = null;
        RegisterRequested = null;
        ManageRequested = null;
        SignOutRequested = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Root.Popdown();
        if (Root.GetParent() is not null) Root.Unparent();
    }

#if DEBUG
    internal string Describe() => $"open={Root.GetVisible()} state={_stack.GetVisibleChildName()} name='{_name.GetText()}' busy={_spinner.GetVisible()}";

    internal void DebugClick(string state)
    {
        var index = state switch { "signin" => 0, "register" => 1, "manage" => 2, "signout" => 3, _ => -1 };
        if (index >= 0 && index < _buttons.Count) _buttons[index].Activate();
    }
#endif

    private Gtk.Widget SignedOut()
    {
        var box = Section();
        box.Append(Hero(Title(Translator.WinoAccount_Titlebar_SignedOutTitle), Caption(Translator.WinoAccount_Titlebar_SignedOutDescription)));
        box.Append(Benefit(WinoIconGlyph.Key, Translator.WinoAccount_Titlebar_SyncBenefitTitle, Translator.WinoAccount_Titlebar_SyncBenefitDescription));
        box.Append(Benefit(WinoIconGlyph.Info, Translator.WinoAccount_Titlebar_AddonsBenefitTitle, Translator.WinoAccount_Titlebar_AddonsBenefitDescription));
        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        actions.SetHomogeneous(true);
        var signIn = Button(Translator.WinoAccount_LoginButton_Title, () => SignInRequested);
        signIn.AddCssClass("suggested-action");
        actions.Append(signIn);
        actions.Append(Button(Translator.WinoAccount_RegisterButton_Title, () => RegisterRequested));
        box.Append(actions);
        return box;
    }

    private Gtk.Widget SignedIn()
    {
        var box = Section();
        _name.SetXalign(0);
        _name.SetEllipsize(Pango.EllipsizeMode.End);
        _name.AddCssClass("heading");
        _email.SetXalign(0);
        _email.SetEllipsize(Pango.EllipsizeMode.Middle);
        _email.AddCssClass("dim-label");
        var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        texts.SetValign(Gtk.Align.Center);
        texts.SetHexpand(true);
        texts.Append(_name);
        texts.Append(_email);
        var profile = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
        profile.Append(_picture.Root);
        profile.Append(texts);
        box.Append(Hero(profile));
        box.Append(Button(Translator.WinoAccount_Titlebar_ManageAccount, () => ManageRequested));
        box.Append(Button(Translator.WinoAccount_SignOutButton_Action, () => SignOutRequested));
        return box;
    }

    private static Gtk.Box Section()
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetMarginTop(8);
        box.SetMarginBottom(8);
        box.SetMarginStart(8);
        box.SetMarginEnd(8);
        return box;
    }

    private static Gtk.Widget Hero(params Gtk.Widget[] content)
    {
        var hero = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        hero.SetMarginBottom(4);
        foreach (var child in content) hero.Append(child);
        return hero;
    }

    private static Gtk.Label Title(string text)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.SetWrap(true);
        label.AddCssClass("title-3");
        return label;
    }

    private static Gtk.Label Caption(string text)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetMaxWidthChars(36);
        label.AddCssClass("dim-label");
        return label;
    }

    private static Gtk.Widget Benefit(WinoIconGlyph glyph, string title, string description)
    {
        var icon = new WinoIcon(glyph, 16).Root;
        icon.SetValign(Gtk.Align.Start);
        icon.AddCssClass("accent");
        var heading = Gtk.Label.New(title);
        heading.SetXalign(0);
        heading.SetWrap(true);
        heading.AddCssClass("heading");
        var body = Gtk.Label.New(description);
        body.SetXalign(0);
        body.SetWrap(true);
        body.SetMaxWidthChars(32);
        body.AddCssClass("caption");
        body.AddCssClass("dim-label");
        var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        texts.SetHexpand(true);
        texts.Append(heading);
        texts.Append(body);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.AddCssClass("card");
        foreach (var margin in (Gtk.Widget[])[icon, texts])
        {
            margin.SetMarginTop(12);
            margin.SetMarginBottom(12);
        }
        icon.SetMarginStart(12);
        texts.SetMarginEnd(12);
        row.Append(icon);
        row.Append(texts);
        return row;
    }

    private Gtk.Button Button(string title, Func<Action?> handler)
    {
        var button = Gtk.Button.NewWithLabel(title);
        WinoA11y.Rename(button, title);
        _subscriptions.Add(GtkSignals.OnClicked(button, () =>
        {
            Root.Popdown();
            handler()?.Invoke();
        }));
        _buttons.Add(button);
        return button;
    }
}
