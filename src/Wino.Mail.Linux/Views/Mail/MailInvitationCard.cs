using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The calendar invitation card above the reader's body (Windows <c>MailInvitationCard</c>, macOS reader invitation):
/// a header row with the event, the answers and Open in calendar that expands into the status, the facts, the reply
/// message and Remove from calendar. It only renders <see cref="MailInvitationCardViewModel"/> and runs its commands.
/// </summary>
internal sealed class MailInvitationCard : IDisposable
{
    private readonly MailInvitationCardViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly MainLoop.Coalescer _apply;
    private readonly PropertyChangedEventHandler _propertyChanged;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Label _icon;
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _detail = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _busy = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Answer _accept;
    private readonly Answer _tentative;
    private readonly Answer _decline;
    private readonly Gtk.Button _open = Gtk.Button.New();
    private readonly Gtk.Button _chevron;
    private readonly Adw.Bin _errorHost = Adw.Bin.New();
    private readonly WinoInfoBar _errorBar;
    private readonly Gtk.Revealer _revealer = Gtk.Revealer.New();
    private readonly Gtk.Box _statusBlock = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.Label _status = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _reason = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _facts = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Fact _date;
    private readonly Fact _recurring;
    private readonly Fact _location;
    private readonly Fact _organizer;
    private readonly Gtk.Overlay _comment = Gtk.Overlay.New();
    private readonly Gtk.TextView _commentView = Gtk.TextView.New();
    private readonly Gtk.Label _commentPlaceholder = Gtk.Label.New(Translator.CalendarEventRsvpPanel_AddMessage);
    private readonly Gtk.Button _remove = Gtk.Button.New();
    private bool _expanded;
    private bool _answersCompact;
    private bool _collapsePending = true;
    private bool _applying;
    private bool _disposed;

    public MailInvitationCard(MailInvitationCardViewModel viewModel, IDispatcher dispatcher, Action<Exception> error)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _error = error;
        _apply = new MainLoop.Coalescer(Apply);
        WinoLiveObjects.Track(this);

        _root.AddCssClass("card");
        _root.AddCssClass("wino-invitation");
        _root.SetOverflow(Gtk.Overflow.Hidden);
        _root.AccessibleRole = Gtk.AccessibleRole.Group;
        WinoA11y.Label(_root, Translator.MailInvitation_CardAutomationName);
        _root.SetVisible(false);

        _icon = WinoIcons.Label(viewModel.HeaderIcon, 16);
        _icon.AddCssClass("accent");
        _title.AddCssClass("heading");
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        _title.SetMaxWidthChars(32);
        _title.SetXalign(0);
        _detail.AddCssClass("caption");
        _detail.AddCssClass("dim-label");
        _detail.SetEllipsize(Pango.EllipsizeMode.End);
        _detail.SetHexpand(true);
        _detail.SetXalign(0);
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(12, 12);
        var busyLabel = Gtk.Label.New(Translator.MailInvitation_Busy);
        busyLabel.AddCssClass("caption");
        busyLabel.AddCssClass("dim-label");
        _busy.Append(spinner);
        _busy.Append(busyLabel);
        _busy.SetHexpand(true);
        _busy.SetVisible(false);
        var toggleContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        toggleContent.Append(_icon);
        toggleContent.Append(_title);
        toggleContent.Append(_detail);
        toggleContent.Append(_busy);
        var toggle = Gtk.Button.New();
        toggle.SetChild(toggleContent);
        toggle.AddCssClass("flat");
        toggle.SetHexpand(true);
        WinoA11y.Label(toggle, Translator.MailInvitation_CardAutomationName);

        _accept = new Answer(WinoIconGlyph.EventAccept, "success", Translator.CalendarEventRsvpPanel_Accept);
        _tentative = new Answer(WinoIconGlyph.EventTentative, "warning", Translator.CalendarEventRsvpPanel_Tentative);
        _decline = new Answer(WinoIconGlyph.EventDecline, "error", Translator.CalendarEventRsvpPanel_Decline);

        _open.SetLabel(Translator.MailInvitation_OpenInCalendar);
        _open.AddCssClass("flat");
        _open.AddCssClass("caption");
        _open.AddCssClass("accent");
        _open.SetVisible(false);
        _chevron = WinoIcons.Button(WinoIconGlyph.ChevronDown, Translator.MailInvitation_CardAutomationName, 12);
        _chevron.SetValign(Gtk.Align.Center);

        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        header.AddCssClass("wino-invitation-header");
        header.SetSizeRequest(-1, 40);
        header.SetMarginTop(5);
        header.SetMarginEnd(12);
        header.SetMarginBottom(5);
        header.SetMarginStart(6);
        header.Append(toggle);
        foreach (var answer in new[] { _accept, _tentative, _decline }) header.Append(answer.Button);
        header.Append(_open);
        header.Append(_chevron);

        _errorBar = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.MailInvitation_ActionFailedTitle, null) { IsClosable = false };
        _errorHost.SetChild(_errorBar.Root);
        _errorHost.SetMarginEnd(8);
        _errorHost.SetMarginBottom(8);
        _errorHost.SetMarginStart(8);
        _errorHost.SetVisible(false);

        _status.AddCssClass("heading");
        _reason.AddCssClass("dim-label");
        foreach (var label in new[] { _status, _reason })
        {
            label.SetWrap(true);
            label.SetWrapMode(Pango.WrapMode.WordChar);
            label.SetXalign(0);
            _statusBlock.Append(label);
        }
        _date = new Fact(WinoIconGlyph.Clock);
        _recurring = new Fact(WinoIconGlyph.CalendarEventRepeat);
        _location = new Fact(WinoIconGlyph.Location);
        _organizer = new Fact(WinoIconGlyph.Person);
        _facts.AddCssClass("wino-invitation-facts");
        foreach (var fact in new[] { _date, _recurring, _location, _organizer }) _facts.Append(fact.Root);

        _commentView.SetWrapMode(Gtk.WrapMode.WordChar);
        _commentView.SetTopMargin(6);
        _commentView.SetBottomMargin(6);
        _commentView.SetLeftMargin(6);
        _commentView.SetRightMargin(6);
        WinoA11y.Label(_commentView, Translator.CalendarEventRsvpPanel_Message);
        var commentScroller = Gtk.ScrolledWindow.New();
        commentScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        commentScroller.SetMinContentHeight(44);
        commentScroller.SetMaxContentHeight(120);
        commentScroller.SetPropagateNaturalHeight(true);
        commentScroller.SetHasFrame(true);
        commentScroller.SetChild(_commentView);
        _commentPlaceholder.AddCssClass("dim-label");
        _commentPlaceholder.SetHalign(Gtk.Align.Start);
        _commentPlaceholder.SetValign(Gtk.Align.Start);
        _commentPlaceholder.SetMarginTop(8);
        _commentPlaceholder.SetMarginStart(8);
        _commentPlaceholder.SetCanTarget(false);
        _commentPlaceholder.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _comment.SetChild(commentScroller);
        _comment.AddOverlay(_commentPlaceholder);

        _remove.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Delete, Translator.MailInvitation_RemoveFromCalendar, 14));
        WinoA11y.Label(_remove, Translator.MailInvitation_RemoveFromCalendar);
        _remove.SetHalign(Gtk.Align.Start);
        _remove.SetVisible(false);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        body.SetMarginTop(12);
        body.SetMarginEnd(12);
        body.SetMarginBottom(12);
        body.SetMarginStart(12);
        body.Append(_statusBlock);
        body.Append(_facts);
        body.Append(_comment);
        body.Append(_remove);
        var bodyBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        bodyBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        bodyBox.Append(body);
        _revealer.SetTransitionType(Gtk.RevealerTransitionType.SlideDown);
        _revealer.SetTransitionDuration(150);
        _revealer.SetRevealChild(false);
        _revealer.SetChild(bodyBox);

        _root.Append(header);
        _root.Append(_errorHost);
        _root.Append(_revealer);

        _subscriptions.Add(GtkSignals.OnClicked(toggle, Toggle));
        _subscriptions.Add(GtkSignals.OnClicked(_chevron, Toggle));
        // The response command is generic: its object overloads throw for a null value-type parameter, so the typed members run it.
        _subscriptions.Add(GtkSignals.OnClicked(_accept.Button, () => Respond(AttendeeStatus.Accepted)));
        _subscriptions.Add(GtkSignals.OnClicked(_tentative.Button, () => Respond(AttendeeStatus.Tentative)));
        _subscriptions.Add(GtkSignals.OnClicked(_decline.Button, () => Respond(AttendeeStatus.Declined)));
        _subscriptions.Add(GtkSignals.OnClicked(_open, () => Run(_viewModel.OpenInCalendarCommand)));
        _subscriptions.Add(GtkSignals.OnClicked(_remove, () => Run(_viewModel.RemoveFromCalendarCommand)));
        var buffer = _commentView.GetBuffer();
        _subscriptions.Add(GtkSignals.Connect<Gtk.TextBuffer>(h => buffer.OnChanged += h, h => buffer.OnChanged -= h, (_, _) => CommentChanged()));

        // The properties change one by one while an invitation loads: one apply per main-loop turn.
        _propertyChanged = (sender, args) =>
        {
            var hidden = args.PropertyName == nameof(MailInvitationCardViewModel.IsVisible) && sender is MailInvitationCardViewModel { IsVisible: false };
            _ = _dispatcher.ExecuteOnUIThread(() =>
            {
                if (hidden) _collapsePending = true;
                _apply.Request();
            });
        };
        _viewModel.PropertyChanged += _propertyChanged;
        Apply();
    }

    public Gtk.Widget Root => _root;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Apply();
        }
    }

    public bool AnswersCompact
    {
        get => _answersCompact;
        set
        {
            if (_answersCompact == value) return;
            _answersCompact = value;
            foreach (var answer in new[] { _accept, _tentative, _decline }) answer.Text.SetVisible(!value);
        }
    }

    public string Describe()
    {
        var vm = _viewModel;
        return $"invitation visible={vm.IsVisible} expanded={_expanded} title=\"{vm.Title}\" icon={vm.HeaderIcon} detail=\"{_detail.GetText()}\" canRespond={vm.CanRespond} " +
            $"busy={vm.IsBusy} accepted={vm.IsAcceptedResponse} tentative={vm.IsTentativeResponse} declined={vm.IsDeclinedResponse} open={vm.CanOpenInCalendar} " +
            $"remove={vm.CanRemoveFromCalendar} status=\"{vm.StatusText}\" reason=\"{vm.ReasonText}\" date=\"{vm.DateRangeText}\" location=\"{vm.Location}\" " +
            $"organizer=\"{vm.OrganizerText}\" recurring={vm.IsRecurring} error=\"{vm.ErrorMessage}\" compact={_answersCompact} comment=\"{vm.Comment}\"";
    }

#if DEBUG
    internal void DebugRespond(AttendeeStatus response) => Respond(response);
    internal void DebugOpen() => Run(_viewModel.OpenInCalendarCommand);
    internal void DebugRemove() => Run(_viewModel.RemoveFromCalendarCommand);
    internal void DebugComment(string text) => _commentView.GetBuffer().SetText(text, -1);
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= _propertyChanged;
        _apply.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _errorBar.Dispose();
    }

    private void Toggle()
    {
        if (!_disposed) IsExpanded = !IsExpanded;
    }

    private void Respond(AttendeeStatus response)
    {
        var command = _viewModel.RespondCommand;
        if (_disposed || !command.CanExecute(response)) return;
        Observe(command.ExecuteAsync(response));
    }

    private void Run(IAsyncRelayCommand command)
    {
        if (_disposed || !command.CanExecute(null)) return;
        Observe(command.ExecuteAsync(null));
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _error(exception); }
    }

    private void CommentChanged()
    {
        var text = _commentView.GetBuffer().Text ?? string.Empty;
        _commentPlaceholder.SetVisible(text.Length == 0);
        if (_applying || _disposed) return;
        if (!string.Equals(_viewModel.Comment, text, StringComparison.Ordinal)) _viewModel.Comment = text;
    }

    private void Apply()
    {
        if (_disposed) return;
        var vm = _viewModel;
        if (_collapsePending)
        {
            _collapsePending = false;
            _expanded = false;
            _revealer.SetRevealChild(false);
        }
        _root.SetVisible(vm.IsVisible);
        if (!vm.IsVisible) return;

        WinoIcons.SetGlyph(_icon, WinoIcons.Glyph(vm.HeaderIcon));
        _title.SetText(vm.Title ?? string.Empty);
        _title.SetTooltipText(vm.Title);
        var detail = vm.CanRespond || string.IsNullOrEmpty(vm.StatusText) ? vm.HeaderSummaryText : vm.StatusText;
        _detail.SetText(detail ?? string.Empty);
        _detail.SetTooltipText(detail);
        _detail.SetVisible(!vm.IsBusy);
        _busy.SetVisible(vm.IsBusy);
        _accept.Apply(vm.CanRespond, vm.IsBusy, vm.IsAcceptedResponse);
        _tentative.Apply(vm.CanRespond, vm.IsBusy, vm.IsTentativeResponse);
        _decline.Apply(vm.CanRespond, vm.IsBusy, vm.IsDeclinedResponse);
        _open.SetVisible(vm.CanOpenInCalendar);
        _open.SetSensitive(vm.CanOpenInCalendar && !vm.IsBusy);
        _errorBar.Message = vm.ErrorMessage;
        _errorBar.IsOpen = vm.HasError;
        _errorHost.SetVisible(vm.HasError);

        var expansion = _expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;
        if (_chevron.GetChild() is Gtk.Label chevronGlyph) WinoIcons.SetGlyph(chevronGlyph, WinoIcons.Glyph(_expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown));
        WinoA11y.Label(_chevron, $"{Translator.MailInvitation_CardAutomationName}, {expansion}");
        _chevron.SetTooltipText(expansion);
        _revealer.SetRevealChild(_expanded);
        if (!_expanded) return;

        _status.SetText(vm.StatusText ?? string.Empty);
        _status.SetVisible(vm.HasStatus);
        _reason.SetText(vm.ReasonText ?? string.Empty);
        _reason.SetVisible(vm.HasReason);
        _statusBlock.SetVisible(vm.HasStatus || vm.HasReason);
        _date.Apply(vm.DateRangeText, vm.HasDateRange);
        _recurring.Apply(Translator.MailInvitation_Recurring, vm.IsRecurring);
        _location.Apply(vm.Location, vm.HasLocation);
        _organizer.Apply(vm.OrganizerText, vm.HasOrganizer);
        _facts.SetVisible(vm.HasDateRange || vm.IsRecurring || vm.HasLocation || vm.HasOrganizer);
        _comment.SetVisible(vm.CanRespond);
        _commentView.SetEditable(vm.CanEditComment);
        _commentView.SetSensitive(vm.CanEditComment);
        var buffer = _commentView.GetBuffer();
        var comment = vm.Comment ?? string.Empty;
        if (!string.Equals(buffer.Text ?? string.Empty, comment, StringComparison.Ordinal))
        {
            _applying = true;
            try { buffer.SetText(comment, -1); }
            finally { _applying = false; }
        }
        _commentPlaceholder.SetVisible(comment.Length == 0);
        _remove.SetVisible(vm.CanRemoveFromCalendar);
        _remove.SetSensitive(vm.CanRemoveFromCalendar && !vm.IsBusy);
    }

    private sealed class Answer
    {
        private readonly Gtk.Label _check = WinoIcons.Label(WinoIconGlyph.Checkmark, 10);

        public Answer(WinoIconGlyph glyph, string tint, string title)
        {
            var icon = WinoIcons.Label(glyph, 14);
            icon.AddCssClass(tint);
            Text = Gtk.Label.New(title);
            Text.AddCssClass("caption");
            _check.SetVisible(false);
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            content.Append(icon);
            content.Append(Text);
            content.Append(_check);
            Button = Gtk.Button.New();
            Button.SetChild(content);
            Button.SetValign(Gtk.Align.Center);
            Button.SetVisible(false);
            WinoA11y.LabelWithTooltip(Button, title);
        }

        public Gtk.Button Button { get; }
        public Gtk.Label Text { get; }

        public void Apply(bool canRespond, bool busy, bool current)
        {
            Button.SetVisible(canRespond);
            Button.SetSensitive(canRespond && !busy);
            _check.SetVisible(current);
        }
    }

    private sealed class Fact
    {
        private readonly Gtk.Label _text = Gtk.Label.New(string.Empty);

        public Fact(WinoIconGlyph glyph)
        {
            Root = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            var icon = WinoIcons.Label(glyph, 14);
            icon.SetValign(Gtk.Align.Start);
            _text.SetWrap(true);
            _text.SetWrapMode(Pango.WrapMode.WordChar);
            _text.SetSelectable(true);
            _text.SetXalign(0);
            _text.SetHexpand(true);
            Root.Append(icon);
            Root.Append(_text);
        }

        public Gtk.Box Root { get; }

        public void Apply(string? text, bool visible)
        {
            _text.SetText(text ?? string.Empty);
            Root.SetVisible(visible);
        }
    }
}
