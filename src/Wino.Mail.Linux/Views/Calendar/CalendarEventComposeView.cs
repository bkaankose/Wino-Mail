using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The event composer (Windows CalendarEventComposePage, macOS CalendarEventComposePageViewController) in the calendar
/// page's cover slot: a command bar (calendar, Show as, Reminder, Private, online meeting, Discard, Save or Send), the
/// form (title, when with the time zone, repeat, location, rich-text notes) and the side pane with attendees and
/// attachments, which moves under the form when the cover is narrower than 760 px. No discard confirmation: neither
/// reference asks and the ViewModel has no dirty state.
/// </summary>
internal sealed partial class CalendarEventComposeView : WinoView<CalendarEventComposePageViewModel>, ICalendarCoverView
{
    private const int NarrowBelowWidth = 760;
    private const int SidePaneWidth = 340;

    private readonly IExternalLauncher _launcher;
    private readonly IPreferencesService _preferences;
    private readonly ITranslationService _translations;
    private readonly Adw.BreakpointBin _root = Adw.BreakpointBin.New();
    private readonly Gtk.Label _heading = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _discard = Gtk.Button.NewWithLabel(Translator.Buttons_Discard);
    private readonly Gtk.Button _save = Gtk.Button.NewWithLabel(Translator.Buttons_Save);
    private readonly Adw.Banner _quota = Adw.Banner.New(string.Empty);
    private readonly Gtk.Box _body = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Separator _separator = Gtk.Separator.New(Gtk.Orientation.Vertical);
    private readonly Gtk.Box _form = Gtk.Box.New(Gtk.Orientation.Vertical, 20);
    private readonly Gtk.ScrolledWindow _sideScroller = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _side = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
    private readonly Gtk.Box _repeatHost = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Box _attendeesHost = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private bool _saving;
    private bool _narrow;

    public CalendarEventComposeView(CalendarEventComposePageViewModel viewModel, WinoViewServices services, IExternalLauncher launcher,
        IPreferencesService preferences, ITranslationService translations)
        : base(viewModel, services)
    {
        _launcher = launcher;
        _preferences = preferences;
        _translations = translations;

        var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        bar.AddCssClass("toolbar");
        _heading.AddCssClass("heading");
        _heading.SetEllipsize(Pango.EllipsizeMode.End);
        _heading.SetWidthChars(10);
        bar.Append(_heading);
        BuildCommandBar(bar);
        var commandScroller = Gtk.ScrolledWindow.New();
        commandScroller.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        commandScroller.SetHexpand(true);
        commandScroller.SetChild(bar);
        _save.AddCssClass("suggested-action");
        // Discard and Save stay in view; only the commands scroll in a narrow window.
        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        toolbar.SetMarginTop(8);
        toolbar.SetMarginBottom(8);
        toolbar.SetMarginStart(12);
        toolbar.SetMarginEnd(12);
        toolbar.Append(commandScroller);
        toolbar.Append(_discard);
        toolbar.Append(_save);

        _quota.SetRevealed(false);

        _form.SetMarginTop(24);
        _form.SetMarginBottom(32);
        _form.SetMarginStart(32);
        _form.SetMarginEnd(32);
        BuildForm(_form);
        var formScroller = Gtk.ScrolledWindow.New();
        formScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        formScroller.SetHexpand(true);
        formScroller.SetVexpand(true);
        formScroller.SetChild(_form);

        _side.SetMarginTop(20);
        _side.SetMarginBottom(20);
        _side.SetMarginStart(20);
        _side.SetMarginEnd(20);
        _side.Append(_attendeesHost);
        BuildAttachments(_side);
        _sideScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _sideScroller.SetSizeRequest(SidePaneWidth, -1);
        _sideScroller.SetChild(_side);

        _body.Append(formScroller);
        _body.Append(_separator);
        _body.Append(_sideScroller);
        _body.SetVexpand(true);

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        column.Append(toolbar);
        column.Append(_quota);
        column.Append(_body);
        _root.SetSizeRequest(360, 300);
        _root.SetChild(column);
        var breakpoint = Adw.Breakpoint.New(Adw.BreakpointCondition.NewLength(Adw.BreakpointConditionLengthType.MaxWidth, NarrowBelowWidth - 1, Adw.LengthUnit.Px));
        Bindings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnApply += h, h => breakpoint.OnApply -= h, (_, _) => SetNarrow(true)));
        Bindings.Own(GtkSignals.Connect<Adw.Breakpoint>(h => breakpoint.OnUnapply += h, h => breakpoint.OnUnapply -= h, (_, _) => SetNarrow(false)));
        _root.AddBreakpoint(breakpoint);

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _root.AddController(keys);
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h,
            (_, args) => HandleKey(args.Keyval, args.State), false));
    }

    public override Gtk.Widget Root => _root;

    public event EventHandler? CloseRequested;

    partial void BuildRecurrence(Gtk.Box host);
    partial void BuildAttendees(Gtk.Box host);
    partial void ReleaseAttendees();

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _originalNotesHtml = (parameter as CalendarEventComposeNavigationArgs)?.NotesHtml ?? string.Empty;
        ViewModel.GetHtmlNotesAsync = GetNotesHtmlAsync;

        Bind.Bind(nameof(CalendarEventComposePageViewModel.EditPageTitle), static vm => vm.EditPageTitle, text => _heading.SetText(string.IsNullOrEmpty(text) ? Translator.CalendarEventCompose_NewEventButton : text));
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SaveButtonText), static vm => vm.SaveButtonText, text => _save.SetLabel(text ?? Translator.Buttons_Save));
        Bind.Bind(nameof(CalendarEventComposePageViewModel.HasQuotaWarning), static vm => vm.HasQuotaWarning, _ => ApplyQuota());
        Bindings.Own(new CommandBinding(ViewModel.CreateCommand, static () => null, enabled => _save.SetSensitive(enabled), Dispatcher, ReportError));
        Bindings.Own(GtkSignals.OnClicked(_save, () => Observe(SaveAsync())));
        Bindings.Own(GtkSignals.OnClicked(_discard, Discard));
        BindFields();
        BuildRecurrence(_repeatHost);
        BuildAttendees(_attendeesHost);
        _repeatHost.SetVisible(_repeatHost.GetFirstChild() is not null);
        _attendeesHost.SetVisible(_attendeesHost.GetFirstChild() is not null);
        StartNotes();

        await ViewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override Task LoadedAsync()
    {
        _title.GrabFocus();
        return Task.CompletedTask;
    }

    protected override async Task DeactivateAsync()
    {
        ReleaseAttendees();
        ReleaseFields();
        await DisposeNotesAsync();
        await base.DeactivateAsync();
    }

    /// <summary>The Save button's body: the ViewModel creates or updates the event, then the cover closes on success.</summary>
    internal async Task SaveAsync()
    {
        if (_saving || !_save.GetSensitive()) return;
        _saving = true;
        try { await ViewModel.CreateCommand.ExecuteAsync(null); }
        finally { _saving = false; }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased && ViewModel.LastCreationSucceeded) CloseRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    internal void Discard() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Ctrl+Return saves (the Mac's Cmd+Return, the mail composer's send key); a plain Return belongs to the notes.</summary>
    private bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        if ((state & Gdk.ModifierType.ControlMask) == 0 || keyval is not (Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter)) return false;
        if (_save.GetSensitive()) Observe(SaveAsync());
        return true;
    }

    private void ApplyQuota()
    {
        _quota.SetTitle(ViewModel.QuotaWarningText ?? string.Empty);
        _quota.SetRevealed(ViewModel.HasQuotaWarning);
    }

    private void SetNarrow(bool narrow)
    {
        if (_narrow == narrow) return;
        _narrow = narrow;
        // A narrow form scrolls the attendees and attachments under the notes; a pane of their own would leave the notes a sliver.
        if (narrow)
        {
            _sideScroller.SetChild(null);
            _form.Append(_side);
        }
        else
        {
            _form.Remove(_side);
            _sideScroller.SetChild(_side);
        }
        _separator.SetVisible(!narrow);
        _sideScroller.SetVisible(!narrow);
        _side.SetMarginStart(narrow ? 0 : 20);
        _side.SetMarginEnd(narrow ? 0 : 20);
        _side.SetMarginTop(narrow ? 0 : 20);
        _side.SetMarginBottom(narrow ? 0 : 20);
        _form.SetMarginTop(narrow ? 16 : 24);
        _form.SetMarginBottom(narrow ? 24 : 32);
        _form.SetMarginStart(narrow ? 20 : 32);
        _form.SetMarginEnd(narrow ? 20 : 32);
    }

    /// <summary>A glyph column beside a content widget (the form rows).</summary>
    private static Gtk.Box Row(WinoIconGlyph glyph, Gtk.Widget content)
    {
        var icon = WinoIcons.Label(glyph, 16);
        icon.AddCssClass("dim-label");
        icon.SetValign(Gtk.Align.Start);
        icon.SetMarginTop(6);
        icon.SetSizeRequest(20, -1);
        content.SetHexpand(true);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.Append(icon);
        row.Append(content);
        return row;
    }
}
