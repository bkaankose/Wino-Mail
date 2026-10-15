using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// A form dialog for the dialog service (the iOS FormSheet and WinoAccountSheet, the Windows ContentDialogs): a
/// Material dialog with the title, an optional description, the fields in the Windows order, an inline error line, Cancel
/// and the consequence-led primary button. The primary button runs <see cref="Primary"/> without closing the dialog, so
/// validation errors and server errors appear in place; <see cref="Finish"/> closes it with a result. While work runs the
/// buttons are disabled and a progress bar shows. Cancel, Back or a tap outside answer like Cancel (the presenter's
/// unavailable value). Build on the main thread inside <see cref="AndroidDialogService"/>'s presentation gate.
/// </summary>
public sealed class AndroidFormDialog<T>
{
    private readonly Context _context;
    private readonly LinearLayout _content;
    private readonly TextView _error;
    private readonly LinearProgressIndicator _progress;
    private readonly string _title;
    private readonly string? _description;
    private AlertDialog? _dialog;
    private TaskCompletionSource<T>? _completion;
    private bool _busy;

    public AndroidFormDialog(Context context, string title, string? description, string primary, string? secondary = null)
    {
        _context = context;
        _title = title;
        _description = description;
        PrimaryText = primary;
        SecondaryText = secondary ?? Translator.Buttons_Cancel;
        _content = new LinearLayout(context) { Orientation = Orientation.Vertical };
        int pad = WinoTokens.Px(context, Resource.Dimension.wino_space_2xl);
        _content.SetPadding(pad, WinoTokens.Px(context, Resource.Dimension.wino_space_2xs), pad, 0);
        Body = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _content.AddView(Body);
        _error = WinoTokens.Style(new TextView(context) { Visibility = ViewStates.Gone }, WinoTextStyle.Description);
        _error.SetTextColor(WinoColors.Critical(context));
        // TalkBack reads a new error when it appears.
        _error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _error.SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_space_s), 0, 0);
        _content.AddView(_error);
        _progress = new LinearProgressIndicator(context) { Indeterminate = true, Visibility = ViewStates.Invisible };
        _content.AddView(_progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
    }

    public Context Context => _context;

    /// <summary>The fields, in order.</summary>
    public LinearLayout Body { get; }

    public string PrimaryText { get; set; }
    public string SecondaryText { get; set; }

    /// <summary>Runs for the primary button; call <see cref="Finish"/> or <see cref="ShowError"/>.</summary>
    public Func<Task>? Primary { get; set; }

    /// <summary>The primary button is enabled while this is true (re-read after every edit and <see cref="Refresh"/>).</summary>
    public Func<bool>? CanConfirm { get; set; }

    /// <summary>Runs when the dialog closes for any reason (clear secrets).</summary>
    public Action? Closed { get; set; }

    public bool IsBusy => _busy;

    public TView Add<TView>(TView view, int topMargin = 0) where TView : View
    {
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        if (Body.ChildCount > 0) layout.TopMargin = WinoTokens.Px(_context, topMargin == 0 ? Resource.Dimension.wino_space_m : topMargin);
        Body.AddView(view, layout);
        return view;
    }

    /// <summary>Body text (secondary colour) in the field order.</summary>
    public TextView AddText(string text, bool secondary = true)
    {
        var label = WinoTokens.Style(new TextView(_context) { Text = text }, secondary ? WinoTextStyle.BodySecondary : WinoTextStyle.Body);
        return Add(label);
    }

    /// <summary>An outlined text field with its label as the hint; edits refresh the primary button and clear the error.</summary>
    public EditText AddTextField(string label, string? text = null, bool email = false, bool password = false, bool multiline = false, string? helper = null)
    {
        var layout = SettingsBinder.CreateTextField(_context, label, multiline, password);
        if (!string.IsNullOrEmpty(helper)) layout.HelperText = helper;
        var field = layout.EditText ?? throw new InvalidOperationException("The text field has no editor.");
        if (email) field.InputType = InputTypes.ClassText | InputTypes.TextVariationEmailAddress;
        field.Text = text ?? string.Empty;
        field.TextChanged += (_, _) =>
        {
            ShowError(null);
            Refresh();
        };
        Add(layout);
        return field;
    }

    /// <summary>A labelled dropdown over fixed items; read <see cref="FormPopUp.SelectedIndex"/>.</summary>
    public FormPopUp AddPopUp(string label, IReadOnlyList<string> items, int selected)
    {
        var caption = WinoTokens.Style(new TextView(_context) { Text = label }, WinoTextStyle.Caption);
        Add(caption);
        var button = SettingsBinder.CreateDropdown(_context, 0);
        button.ContentDescription = label;
        var popUp = new FormPopUp(button, items, selected);
        popUp.Changed += (_, _) => { ShowError(null); Refresh(); };
        Add(button, Resource.Dimension.wino_space_2xs);
        return popUp;
    }

    public void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Visibility = string.IsNullOrWhiteSpace(message) ? ViewStates.Gone : ViewStates.Visible;
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Invisible;
        _dialog?.SetCancelable(!busy);
        Refresh();
    }

    public void Refresh()
    {
        if (_dialog is null) return;
        if (_dialog.GetButton((int)DialogButtonType.Positive) is { } primary)
        {
            primary.Text = PrimaryText;
            primary.Enabled = !_busy && (CanConfirm?.Invoke() ?? true);
        }
        if (_dialog.GetButton((int)DialogButtonType.Negative) is { } secondary)
        {
            secondary.Text = SecondaryText;
            secondary.Enabled = !_busy;
        }
    }

    /// <summary>Closes the dialog with <paramref name="result"/>.</summary>
    public void Finish(T result)
    {
        _completion?.TrySetResult(result);
        _dialog?.Dismiss();
    }

    public string Title
    {
        set => _dialog?.SetTitle(value);
    }

    /// <summary>Builds the dialog for the presenter; the positive button never closes it by itself.</summary>
    public Dialog Build(TaskCompletionSource<T> completion)
    {
        _completion = completion;
        var scroll = new ScrollView(_context);
        scroll.AddView(_content);
        var builder = new MaterialAlertDialogBuilder(_context)
            .SetTitle(_title)
            .SetView(scroll)
            .SetPositiveButton(PrimaryText, (IDialogInterfaceOnClickListener?)null)
            .SetNegativeButton(SecondaryText, (_, _) => { });
        if (!string.IsNullOrWhiteSpace(_description)) builder.SetMessage(_description);
        var dialog = builder.Create();
        _dialog = dialog;
        dialog.ShowEvent += (_, _) =>
        {
            if (dialog.GetButton((int)DialogButtonType.Positive) is { } primary)
                primary.Click += async (_, _) =>
                {
                    if (_busy || !(CanConfirm?.Invoke() ?? true) || Primary is null) return;
                    try { await Primary(); }
                    catch (Exception exception)
                    {
                        Serilog.Log.Warning(exception, "A form dialog action failed.");
                        SetBusy(false);
                        ShowError(exception.Message);
                    }
                };
            Refresh();
        };
        dialog.DismissEvent += (_, _) => Closed?.Invoke();
        // Fields stay above the keyboard.
        dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
        return dialog;
    }
}

/// <summary>A dropdown inside a form dialog.</summary>
public sealed class FormPopUp
{
    private readonly Google.Android.Material.Button.MaterialButton _button;
    private IReadOnlyList<string> _items;

    public FormPopUp(Google.Android.Material.Button.MaterialButton button, IReadOnlyList<string> items, int selected)
    {
        _button = button;
        _items = items;
        SelectedIndex = selected;
        Render();
        button.Click += (_, _) =>
        {
            if (_items.Count == 0 || button.Context is not { } context) return;
            WinoChoiceSheet.Show(context, button.ContentDescription, _items, SelectedIndex, index =>
            {
                SelectedIndex = index;
                Render();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        };
    }

    public int SelectedIndex { get; private set; }

    public event EventHandler? Changed;

    public void SetItems(IReadOnlyList<string> items, int selected)
    {
        _items = items;
        SelectedIndex = selected;
        Render();
    }

    private void Render() => _button.Text = SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : string.Empty;
}
