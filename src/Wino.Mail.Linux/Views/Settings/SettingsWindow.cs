using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The Settings window: a sidebar of catalog pages beside one page slot with the breadcrumb and description above it.
/// It is not transient for the main window and not added to the application, so it never keeps the process alive; it
/// hides on close and is destroyed only by <see cref="Destroy"/>. It is a dialog surface with its own info bar host.
/// </summary>
internal sealed class SettingsWindow : IDialogSurface
{
    public const int DefaultWidth = 940;
    public const int DefaultHeight = 680;
    public const int MinimumWidth = 360;
    public const int MinimumHeight = 400;
    public const int CollapseMaximumWidth = 720;
    private const int ContentClamp = 600;
    private const string ActionGroupName = "settings";

    private readonly Adw.NavigationSplitView _split = Adw.NavigationSplitView.New();
    private readonly Adw.NavigationPage _contentPage;
    private readonly Adw.Bin _slot = Adw.Bin.New();
    private readonly Gtk.Button _back = Gtk.Button.NewFromIconName("go-previous-symbolic");
    private readonly Gtk.Button _rootButton = Gtk.Button.New();
    private readonly Gtk.Label _separator = Gtk.Label.New("›");
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _description = Gtk.Label.New(string.Empty);
    private readonly InfoBarHost _infoBars = new();
    private readonly List<IDisposable> _subscriptions = [];
    private Gio.SimpleAction? _forward;
    private WinoPage _root = SettingsCatalog.DefaultPage;
    private bool _destroyed;

    public SettingsWindow(ISettingsCatalog catalog)
    {
        Sidebar = new SettingsSidebarView(catalog);

        Window = Adw.Window.New();
        Window.SetTitle(Translator.MenuSettings);
        Window.SetDefaultSize(DefaultWidth, DefaultHeight);
        Window.SetSizeRequest(MinimumWidth, MinimumHeight);
        Window.SetHideOnClose(true);

        _split.SetMinSidebarWidth(220);
        _split.SetMaxSidebarWidth(280);
        _split.SetSidebar(Adw.NavigationPage.New(Sidebar.Root, Translator.MenuSettings));
        _contentPage = Adw.NavigationPage.New(BuildContent(), Translator.MenuSettings);
        _split.SetContent(_contentPage);

        _infoBars.Child = _split;
        Window.SetContent(_infoBars.Root);

        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, CollapseMaximumWidth, Adw.LengthUnit.Sp));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) => _split.SetCollapsed(true)));
        _subscriptions.Add(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) => _split.SetCollapsed(false)));
        Window.AddBreakpoint(breakpoint);

        _subscriptions.Add(GtkSignals.OnClicked(_back, () => BackRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_rootButton, () => RootRequested?.Invoke(_root)));
        _subscriptions.Add(GtkSignals.OnNotify(Window, "is-active", () => ActiveChanged?.Invoke(Window.IsActive)));
        // The window lives as long as the app; this returning handler has no GtkSignals overload.
        Window.OnCloseRequest += (_, _) => OnCloseRequest();

        InstallShortcuts();
    }

    public Adw.Window Window { get; }
    public SettingsSidebarView Sidebar { get; }
    public InfoBarHost InfoBars => _infoBars;
    public Gtk.Widget? CurrentPageWidget => _slot.GetChild();
    public string CurrentTitle => _title.GetText();
    public string CurrentDescription => _description.GetText();
    public bool IsCollapsed => _split.GetCollapsed();
    public bool ShowsContent => _split.GetShowContent();
    public bool IsBackVisible => _back.GetVisible();

    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action<WinoPage>? RootRequested;
    public event Action? Closed;
    public event Action<bool>? ActiveChanged;

    /// <summary>Asked before the user closes the window; false keeps it open (a page asks first).</summary>
    public Func<bool>? ShouldClose { get; set; }

    Gtk.Widget IDialogSurface.Owner => Window;

    public void ShowMessage(string title, string message, InfoBarMessageType type, string? actionText, Action? action)
        => _infoBars.Show(title, message, type, actionText, action);

    public void Present()
    {
        if (_destroyed) return;
        if (!Window.GetVisible()) Window.SetFocus(Sidebar.SearchEntry);
        Window.Present();
    }

    /// <summary>Hides the window without asking <see cref="ShouldClose"/> and without raising <see cref="Closed"/>.</summary>
    public void Hide()
    {
        if (!_destroyed) Window.SetVisible(false);
    }

    /// <param name="reveal">In the collapsed layout, show the page instead of the list.</param>
    public void SetPage(Gtk.Widget page, string title, WinoPage current, WinoPage root, string description, bool reveal)
    {
        _slot.SetChild(page);
        SetHeader(title, current, root, description);
        if (reveal && _split.GetCollapsed()) _split.SetShowContent(true);
    }

    public void SetHeader(string title, WinoPage current, WinoPage root, string description)
    {
        _root = root;
        var isSubPage = current != root;
        _title.SetText(title);
        _rootButton.SetLabel(SettingsCatalog.Title(root));
        _rootButton.SetVisible(isSubPage);
        _separator.SetVisible(isSubPage);
        _description.SetText(description);
        _description.SetVisible(!string.IsNullOrWhiteSpace(description));
        _contentPage.SetTitle(title);
    }

    public void ClearPage()
    {
        _slot.SetChild(null);
        _split.SetShowContent(false);
        _title.SetText(string.Empty);
        _description.SetText(string.Empty);
        _rootButton.SetVisible(false);
        _separator.SetVisible(false);
    }

    public void SetNavigationState(bool canGoBack, bool canGoForward)
    {
        _back.SetVisible(canGoBack);
        _forward?.SetEnabled(canGoForward);
    }

    public void SetSearchText(string text) => Sidebar.SearchText = text;

    /// <summary>Destroys the window at shutdown; it cannot be shown again.</summary>
    public void Destroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        ShouldClose = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Sidebar.Dispose();
        _infoBars.Dispose();
        _slot.SetChild(null);
        Window.Destroy();
    }

    private bool OnCloseRequest()
    {
        if (_destroyed) return false;
        if (ShouldClose?.Invoke() == false) return true;
        Closed?.Invoke();
        return false;
    }

    private Gtk.Widget BuildContent()
    {
        _back.AddCssClass("flat");
        WinoA11y.LabelWithTooltip(_back, Translator.Buttons_Back);
        _back.SetVisible(false);
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(Gtk.Label.New(string.Empty));
        header.PackStart(_back);

        _rootButton.AddCssClass("flat");
        _rootButton.SetVisible(false);
        _separator.AddCssClass("dim-label");
        _separator.SetVisible(false);
        _title.AddCssClass("title-2");
        _title.SetXalign(0);
        _title.SetWrap(true);
        var breadcrumb = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        breadcrumb.Append(_rootButton);
        breadcrumb.Append(_separator);
        breadcrumb.Append(_title);

        _description.AddCssClass("dim-label");
        _description.SetXalign(0);
        _description.SetWrap(true);
        _description.SetVisible(false);

        var block = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        block.Append(breadcrumb);
        block.Append(_description);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(ContentClamp);
        clamp.SetChild(block);
        clamp.SetMarginTop(18);
        clamp.SetMarginStart(12);
        clamp.SetMarginEnd(12);

        _slot.SetVexpand(true);
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        body.Append(clamp);
        body.Append(_slot);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(body);
        return view;
    }

    // Escape and Ctrl+W use the window's built-in close action, so they ask ShouldClose like the close button does.
    private void InstallShortcuts()
    {
        var group = Gio.SimpleActionGroup.New();
        AddAction(group, "back", () => BackRequested?.Invoke());
        _forward = AddAction(group, "forward", () => ForwardRequested?.Invoke());
        _forward.SetEnabled(false);
        AddAction(group, "search", Sidebar.FocusSearch);
        Window.InsertActionGroup(ActionGroupName, group);

        var controller = Gtk.ShortcutController.New();
        controller.SetScope(Gtk.ShortcutScope.Global);
        AddShortcut(controller, "Escape", "window.close");
        AddShortcut(controller, "<Control>w", "window.close");
        AddShortcut(controller, "<Alt>Left", ActionGroupName + ".back");
        AddShortcut(controller, "<Alt>Right", ActionGroupName + ".forward");
        AddShortcut(controller, "<Control>f", ActionGroupName + ".search");
        Window.AddController(controller);
    }

    private Gio.SimpleAction AddAction(Gio.SimpleActionGroup group, string name, Action activated)
    {
        var action = Gio.SimpleAction.New(name, null);
        _subscriptions.Add(GtkSignals.OnActivate(action, _ => activated()));
        group.AddAction(action);
        return action;
    }

    private static void AddShortcut(Gtk.ShortcutController controller, string trigger, string action)
    {
        if (Gtk.ShortcutTrigger.ParseString(trigger) is not { } parsed)
        {
            Serilog.Log.Warning("Settings window: the shortcut {Trigger} could not be parsed.", trigger);
            return;
        }
        controller.AddShortcut(Gtk.Shortcut.New(parsed, Gtk.NamedAction.New(action)));
    }
}
