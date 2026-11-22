using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// The contact editor in place of the contact list (Windows ContactEditPage, macOS ContactEditPageViewController): a
/// hero with photo, preview name, favourite, Cancel and Save, a switcher over the five editor sections, and libadwaita
/// preference groups bound to the ViewModel. Only user edits mark the editor dirty; Cancel, Escape and every back
/// route go through <c>BackCommand</c>, which the shared coordinator guards with the discard question.
/// </summary>
internal sealed partial class ContactEditView : WinoView<ContactEditPageViewModel>
{
    private static readonly ContactEditorCategory[] Categories =
    [
        ContactEditorCategory.ContactInformation, ContactEditorCategory.Work, ContactEditorCategory.Address,
        ContactEditorCategory.Other, ContactEditorCategory.Notes
    ];

    private readonly StrongBox<ContactEditView?> _self;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Adw.ViewStack _stack = Adw.ViewStack.New();
    private readonly Adw.Banner _error = Adw.Banner.New(string.Empty);
    private readonly WinoContactPicture _picture = new(72);
    private readonly Gtk.Overlay _pictureOverlay = Gtk.Overlay.New();
    private readonly Gtk.Label _previewName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _previewSubtitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _favorite = Gtk.Button.New();
    private readonly Gtk.Label _favoriteGlyph = WinoIcons.Label(WinoIconGlyph.Star, 18);
    private readonly Gtk.Button _cancel = Gtk.Button.NewWithLabel(Translator.ContactEditor_Cancel);
    private readonly Gtk.Button _save = Gtk.Button.NewWithLabel(Translator.Buttons_Save);
    private readonly Gtk.ScrolledWindow _scroller = Gtk.ScrolledWindow.New();
    private Adw.EntryRow? _givenName;

    public ContactEditView(ContactEditPageViewModel viewModel, WinoViewServices services)
        : base(viewModel, services)
    {
        _self = new StrongBox<ContactEditView?>(this);
        Bindings.Own(new ActionDisposable(() => _self.Value = null));
        _root.SetVexpand(true);
        _root.SetHexpand(true);
        _root.Append(BuildHero());
        _root.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

        var pages = new Func<Gtk.Widget>[] { BuildContactInformation, BuildWork, BuildAddress, BuildOther, BuildNotes };
        string[] titles =
        [
            Translator.ContactEditor_CategoryContactInformation, Translator.ContactEditor_CategoryWork, Translator.ContactEditor_CategoryAddress,
            Translator.ContactEditor_CategoryOther, Translator.ContactEditor_CategoryNotes
        ];
        for (var index = 0; index < pages.Length; index++) _stack.AddTitled(Page(pages[index]()), "c" + index.ToString(CultureInfo.InvariantCulture), titles[index]);

        var switcher = Adw.InlineViewSwitcher.New();
        switcher.SetStack(_stack);
        switcher.SetDisplayMode(Adw.InlineViewSwitcherDisplayMode.Labels);
        switcher.SetHalign(Gtk.Align.Start);
        switcher.SetMarginTop(12);
        switcher.SetMarginStart(24);
        switcher.SetMarginEnd(24);
        switcher.SetMarginBottom(4);
        _root.Append(switcher);

        _error.SetUseMarkup(false);
        _error.SetButtonLabel(Translator.Buttons_Close);
        _root.Append(_error);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(720);
        clamp.SetChild(_stack);
        clamp.SetMarginStart(12);
        clamp.SetMarginEnd(12);
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetVexpand(true);
        _scroller.SetChild(clamp);
        _root.Append(_scroller);
        BuildShortcuts();
    }

    public override Gtk.Widget Root => _root;

    internal Adw.ViewStack Stack => _stack;

    internal Gtk.ScrolledWindow Scroller => _scroller;

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        DebugCurrent = new WeakReference<ContactEditView>(this);
#endif
        BindHero();
        BindForm();
        await ViewModel.InitializeNavigationAsync(mode, parameter);
        if (IsReleased) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased && !ViewModel.IsEditMode) _givenName?.GrabFocus();
        });
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (DebugCurrent is not null && DebugCurrent.TryGetTarget(out var current) && ReferenceEquals(current, this)) DebugCurrent = null;
#endif
        return base.DeactivateAsync();
    }

#if DEBUG
    internal static WeakReference<ContactEditView>? DebugCurrent { get; private set; }

    private readonly Dictionary<string, Gtk.Widget> _debugFields = new(StringComparer.OrdinalIgnoreCase);

    internal Gtk.Widget? DebugField(string name) => _debugFields.TryGetValue(name, out var widget) ? widget : null;

    internal IEnumerable<string> DebugFieldNames => _debugFields.Keys;
#endif

    [Conditional("DEBUG")]
    private void RegisterField(string name, Gtk.Widget widget)
    {
#if DEBUG
        _debugFields[name] = widget;
#endif
    }

    partial void BindDetails();

    private Gtk.Widget BuildHero()
    {
        var hero = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        hero.AddCssClass(WinoCssClass.ContactEditHero);
        hero.SetMarginTop(14);
        hero.SetMarginBottom(14);
        hero.SetMarginStart(24);
        hero.SetMarginEnd(24);

        _pictureOverlay.SetChild(_picture.Root);
        hero.Append(_pictureOverlay);

        var names = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        names.SetHexpand(true);
        names.SetValign(Gtk.Align.Center);
        _previewName.AddCssClass("title-1");
        _previewName.SetXalign(0);
        _previewName.SetEllipsize(Pango.EllipsizeMode.End);
        names.Append(_previewName);
        _previewSubtitle.AddCssClass("dim-label");
        _previewSubtitle.SetXalign(0);
        _previewSubtitle.SetEllipsize(Pango.EllipsizeMode.End);
        names.Append(_previewSubtitle);
        hero.Append(names);

        _favorite.SetChild(_favoriteGlyph);
        _favorite.AddCssClass("flat");
        _favorite.SetValign(Gtk.Align.Center);
        hero.Append(_favorite);
        _cancel.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(_cancel, Translator.ContactEditor_Cancel);
        hero.Append(_cancel);
        _save.AddCssClass("suggested-action");
        _save.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(_save, Translator.Buttons_Save);
        hero.Append(_save);
        BuildPhotoButton();
        return hero;
    }

    partial void BuildPhotoButton();

    private void BindHero()
    {
        Bind.BindAny(static vm => (vm.PreviewDisplayName, vm.PreviewSubtitle), state =>
        {
            _previewName.SetText(state.PreviewDisplayName ?? string.Empty);
            _previewSubtitle.SetText(state.PreviewSubtitle ?? string.Empty);
            _previewSubtitle.SetVisible(!string.IsNullOrWhiteSpace(state.PreviewSubtitle));
            var address = ViewModel.EmailAddresses.FirstOrDefault(static a => !string.IsNullOrWhiteSpace(a?.Address))?.Address;
            _picture.SetIdentity(state.PreviewDisplayName, address ?? state.PreviewDisplayName);
        });
        Bind.Bind(nameof(ContactEditPageViewModel.IsFavorite), static vm => vm.IsFavorite, favorite =>
        {
            WinoIcons.SetGlyph(_favoriteGlyph, WinoIcons.Glyph(favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star));
            if (favorite) _favorite.AddCssClass(WinoCssClass.FavoriteOn);
            else _favorite.RemoveCssClass(WinoCssClass.FavoriteOn);
            WinoA11y.LabelWithTooltip(_favorite, favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite);
        });
        Bind.BindCommand(_favorite, ViewModel.ToggleFavoriteCommand);
        Bind.BindCommand(_save, ViewModel.SaveCommand);
        Bind.BindCommand(_cancel, ViewModel.BackCommand);

        Bind.Bind(nameof(ContactEditPageViewModel.ErrorMessage), static vm => vm.ErrorMessage, text => _error.SetTitle(text ?? string.Empty));
        Bind.Bind(nameof(ContactEditPageViewModel.IsErrorOpen), static vm => vm.IsErrorOpen, _error.SetRevealed);
        var self = _self;
        var banner = _error;
        Bindings.Own(GtkSignals.Connect<Adw.Banner>(h => banner.OnButtonClicked += h, h => banner.OnButtonClicked -= h,
            (_, _) => { if (self.Value is { } view) view.ViewModel.IsErrorOpen = false; }));

        Bind.Bind(nameof(ContactEditPageViewModel.SelectedCategory), static vm => vm.SelectedCategory, category =>
        {
            var index = Array.IndexOf(Categories, category);
            if (index >= 0) _stack.SetVisibleChildName("c" + index.ToString(CultureInfo.InvariantCulture));
        });
        var stack = _stack;
        Bindings.Own(GtkSignals.OnNotify(stack, "visible-child-name", () =>
        {
            if (self.Value is not { } view || stack.GetVisibleChildName() is not { Length: > 1 } name
                || !int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= Categories.Length) return;
            if (view.ViewModel.SelectedCategory != Categories[index]) view.ViewModel.SelectedCategory = Categories[index];
        }));
    }

    private void BindForm()
    {
        BindIdentity();
        BindCommunication();
        BindDetails();
    }

    private void BuildShortcuts()
    {
        var group = Gio.SimpleActionGroup.New();
        var save = Gio.SimpleAction.New("save", null);
        var cancel = Gio.SimpleAction.New("cancel", null);
        group.AddAction(save);
        group.AddAction(cancel);
        _root.InsertActionGroup("contact-edit", group);
        var keys = Gtk.ShortcutController.New();
        keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("<Control>s"), Gtk.NamedAction.New("contact-edit.save")));
        keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("Escape"), Gtk.NamedAction.New("contact-edit.cancel")));
        _root.AddController(keys);
        Bind.BindCommand(save, ViewModel.SaveCommand);
        Bind.BindCommand(cancel, ViewModel.BackCommand);
        var root = _root;
        Bindings.Own(new ActionDisposable(() =>
        {
            root.RemoveController(keys);
            root.InsertActionGroup("contact-edit", null);
        }));
    }

    private static Gtk.Widget Page(Gtk.Widget content)
    {
        content.SetMarginTop(16);
        content.SetMarginBottom(32);
        return content;
    }

    private static Gtk.Box Column() => Gtk.Box.New(Gtk.Orientation.Vertical, 24);

    private static Adw.PreferencesGroup Group(string title, string? description, params Gtk.Widget[] rows)
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(GLib.Functions.MarkupEscapeText(title, -1));
        if (!string.IsNullOrEmpty(description)) group.SetDescription(GLib.Functions.MarkupEscapeText(description, -1));
        foreach (var row in rows) group.Add(row);
        return group;
    }

    private static Adw.EntryRow EntryRow(string header)
    {
        var row = Adw.EntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(header);
        return row;
    }

    /// <summary>A text field over a ViewModel property; the edit marks the editor dirty, loading does not.</summary>
    private Adw.EntryRow Field(string key, string header, string property, Func<ContactEditPageViewModel, string?> read,
        Action<ContactEditPageViewModel, string> write, Gtk.InputPurpose purpose = Gtk.InputPurpose.FreeForm)
    {
        var row = EntryRow(header);
        row.SetInputPurpose(purpose);
        RegisterField(key, row);
        Bind.BindText(row, property, read, (vm, text) =>
        {
            write(vm, text);
            vm.MarkDirty();
        });
        return row;
    }

    /// <summary>A digits-only field over a nullable number; an empty field is null.</summary>
    private Adw.EntryRow IntField(string key, string header, string property, Func<ContactEditPageViewModel, int?> read, Action<ContactEditPageViewModel, int?> write)
        => Field(key, header, property,
            vm => read(vm)?.ToString(CultureInfo.InvariantCulture),
            (vm, text) => write(vm, int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null),
            Gtk.InputPurpose.Digits);

    /// <summary>A text field over a plain entity, which raises no notifications: set once, then written on every change.</summary>
    private Adw.EntryRow EntityField(BindingScope scope, string key, string header, string? value, Action<string> set,
        Gtk.InputPurpose purpose = Gtk.InputPurpose.FreeForm)
    {
        var row = EntryRow(header);
        row.SetInputPurpose(purpose);
        row.SetText(value ?? string.Empty);
        RegisterField(key, row);
        var self = _self;
        scope.Own(GtkSignals.OnChanged(row, () =>
        {
            set(row.GetText());
            self.Value?.ViewModel.MarkDirty();
        }));
        return row;
    }

    private Gtk.Entry SuffixEntry(BindingScope scope, string key, string name, string? value, Action<string> set)
    {
        var entry = Gtk.Entry.New();
        entry.SetWidthChars(10);
        entry.SetMaxWidthChars(12);
        entry.SetPlaceholderText(name);
        entry.SetValign(Gtk.Align.Center);
        entry.SetText(value ?? string.Empty);
        WinoA11y.Label(entry, name);
        RegisterField(key, entry);
        var self = _self;
        scope.Own(GtkSignals.OnChanged(entry, () =>
        {
            set(entry.GetText());
            self.Value?.ViewModel.MarkDirty();
        }));
        return entry;
    }

    private Gtk.Button RemoveButton(BindingScope scope, string key, Action remove)
    {
        var button = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.Buttons_Remove);
        RegisterField(key, button);
        scope.Own(GtkSignals.OnClicked(button, remove));
        return button;
    }

    /// <summary>
    /// Keeps one row per item of <paramref name="items"/>: rows of items that stay keep their widgets (and the focus),
    /// rows of removed items are dropped, new items get a row at their index.
    /// </summary>
    private void Rows<T>(WinoRepeatedFieldGroup group, ObservableCollection<T> items, Func<T, int, BindingScope, Gtk.Widget> build, Action? changed = null,
        int offset = 0)
        where T : class
    {
        var parent = Bindings.Own(new BindingScope());
        var shown = new Dictionary<T, (Gtk.Widget Row, BindingScope Scope)>(ReferenceEqualityComparer.Instance);
        Bind.BindCollection(items, () =>
        {
            var current = new HashSet<T>(items.Where(static item => item is not null), ReferenceEqualityComparer.Instance);
            foreach (var gone in shown.Keys.Where(item => !current.Contains(item)).ToArray())
            {
                var (row, scope) = shown[gone];
                group.Remove(row);
                scope.Dispose();
                shown.Remove(gone);
            }
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item is null || shown.ContainsKey(item)) continue;
                var scope = parent.Own(new BindingScope());
                var row = build(item, index, scope);
                group.Insert(row, index + offset);
                shown[item] = (row, scope);
            }
            changed?.Invoke();
        });
    }
}
