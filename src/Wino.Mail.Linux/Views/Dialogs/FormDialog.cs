using Wino.Core.Domain;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// A small form in an <see cref="Adw.Dialog"/>: Cancel and the primary button in the header bar, an optional description,
/// the caller's fields and an inline error line. Enter confirms through the default widget, Escape cancels, and a closing
/// owner cancels. Build it on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal sealed class FormDialog
{
    private readonly HashSet<char> _mnemonics = [];
    private readonly Gtk.Button _primary;
    private readonly Gtk.Button _cancel;
    private readonly Gtk.Box _start = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.Label _error = Gtk.Label.New(string.Empty);
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly List<IDisposable> _owned = [];
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _presented;
    private bool _confirmed;
    private bool _validating;

    public FormDialog(string title, string? description, string primaryText, bool destructive = false, string? cancelText = null)
    {
        Dialog = Adw.Dialog.New();
        Dialog.SetTitle(title);
        Dialog.SetFollowsContentSize(true);
        Dialog.SetContentWidth(420);

        _cancel = Gtk.Button.NewWithMnemonic(Mnemonics.Assign(cancelText ?? Translator.Buttons_Cancel, _mnemonics));
        _primary = Gtk.Button.NewWithMnemonic(Mnemonics.Assign(primaryText, _mnemonics));
        _primary.AddCssClass(destructive ? "destructive-action" : "suggested-action");
        _spinner.SetVisible(false);

        var header = Adw.HeaderBar.New();
        header.SetShowStartTitleButtons(false);
        header.SetShowEndTitleButtons(false);
        _start.Append(_cancel);
        header.PackStart(_start);
        header.PackEnd(_primary);
        header.PackEnd(_spinner);

        Content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        Content.SetMarginTop(12);
        Content.SetMarginStart(12);
        Content.SetMarginEnd(12);
        Content.SetMarginBottom(18);

        var message = Gtk.Label.New(description ?? string.Empty);
        message.SetWrap(true);
        message.SetXalign(0);
        message.AddCssClass("dim-label");
        message.SetVisible(!string.IsNullOrWhiteSpace(description));
        Content.Append(message);

        _error.SetWrap(true);
        _error.SetXalign(0);
        _error.AddCssClass("error");
        _error.SetVisible(false);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        body.Append(Content);
        body.Append(_error);
        _error.SetMarginStart(12);
        _error.SetMarginEnd(12);
        _error.SetMarginBottom(18);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetPropagateNaturalHeight(true);
        scroll.SetChild(body);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(scroll);
        Dialog.SetChild(view);
        Dialog.SetDefaultWidget(_primary);

        _owned.Add(GtkSignals.OnClicked(_primary, () => _ = ConfirmAsync()));
        _owned.Add(GtkSignals.OnClicked(_cancel, () => Dialog.Close()));
        _owned.Add(GtkSignals.Connect<Adw.Dialog>(h => Dialog.OnClosed += h, h => Dialog.OnClosed -= h, (_, _) => Closed()));
    }

    public Adw.Dialog Dialog { get; }
    public Gtk.Box Content { get; }

    public int ContentWidth { set => Dialog.SetContentWidth(value); }

    public string PrimaryText { set => _primary.SetLabel(value.Replace("_", "__", StringComparison.Ordinal)); }

    /// <summary>Hides the primary button when there is nothing to confirm; Cancel stays.</summary>
    public bool PrimaryVisible { set => _primary.SetVisible(value); }

    public string Title { set => Dialog.SetTitle(value); }

    /// <summary>The primary button's sensitivity; evaluated by <see cref="Refresh"/>.</summary>
    public Func<bool>? CanConfirm { get; set; }

    /// <summary>An error text keeps the dialog open.</summary>
    public Func<string?>? Validate { get; set; }

    /// <summary>Runs after <see cref="Validate"/>, with both buttons insensitive and a spinner showing.</summary>
    public Func<Task<string?>>? ValidateAsync { get; set; }

    public T Add<T>(T widget) where T : Gtk.Widget
    {
        Content.Append(widget);
        return widget;
    }

    public Adw.PreferencesGroup AddGroup(string? title = null)
    {
        var group = Adw.PreferencesGroup.New();
        if (!string.IsNullOrEmpty(title)) group.SetTitle(title);
        return Add(group);
    }

    /// <summary>Packed after Cancel. Connect it through <see cref="GtkSignals"/> and hand the subscription to <see cref="Own"/>.</summary>
    public Gtk.Button AddSecondaryButton(string text)
    {
        var button = Gtk.Button.NewWithMnemonic(Mnemonics.Assign(text, _mnemonics));
        _start.Append(button);
        return button;
    }

    public void SetInitialFocus(Gtk.Widget widget) => Dialog.SetFocus(widget);

    public void Refresh() => _primary.SetSensitive(!_validating && (CanConfirm?.Invoke() ?? true));

    public void ShowError(string? message)
    {
        _error.SetText(message ?? string.Empty);
        _error.SetVisible(!string.IsNullOrEmpty(message));
    }

    /// <summary>Disposed when the dialog closed.</summary>
    public void Own(IDisposable subscription) => _owned.Add(subscription);

    /// <summary>Main thread only, once. True when the primary button confirmed and validation passed.</summary>
    public Task<bool> PresentAsync(Gtk.Widget owner)
    {
        if (_presented) throw new InvalidOperationException("A form dialog is presented once.");
        _presented = true;
        Refresh();
        Dialog.Present(owner);
        return _completion.Task;
    }

#if DEBUG
    internal void DebugConfirm() => _ = ConfirmAsync();

    internal string ErrorText => _error.GetVisible() ? _error.GetText() : string.Empty;
#endif

    public void Close(bool confirmed)
    {
        _confirmed = confirmed;
        Dialog.ForceClose();
    }

    private async Task ConfirmAsync()
    {
        if (_validating || !(CanConfirm?.Invoke() ?? true)) return;
        try
        {
            if (Validate?.Invoke() is { Length: > 0 } error)
            {
                ShowError(error);
                return;
            }

            if (ValidateAsync is { } validateAsync)
            {
                SetValidating(true);
                string? asyncError;
                try { asyncError = await validateAsync(); }
                finally { SetValidating(false); }
                if (_completion.Task.IsCompleted) return;
                if (!string.IsNullOrEmpty(asyncError))
                {
                    ShowError(asyncError);
                    return;
                }
            }

            ShowError(null);
            _confirmed = true;
            Dialog.Close();
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Form dialog validation failed.");
            ShowError(exception.Message);
        }
    }

    private void SetValidating(bool validating)
    {
        _validating = validating;
        _cancel.SetSensitive(!validating);
        _spinner.SetVisible(validating);
        Dialog.SetCanClose(!validating);
        Refresh();
    }

    private void Closed()
    {
        foreach (var subscription in _owned) subscription.Dispose();
        _owned.Clear();
        _completion.TrySetResult(_confirmed);
    }
}
