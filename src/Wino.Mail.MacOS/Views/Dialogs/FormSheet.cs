using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// A small titled form presented as a window sheet: heading, optional description, labelled rows
/// (trailing-aligned labels, Windows ContentDialog field order), an inline error line, then Cancel
/// and the primary button. Return confirms, Escape cancels, closing the parent cancels.
/// Build the form on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal sealed class FormSheet
{
    private readonly NSWindow _sheet;
    private readonly NSGridView _grid = new() { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 10, ColumnSpacing = 12 };
    private readonly NSStackView _stack;
    private readonly NSTextField _error;
    private readonly NSButton _confirm;
    private readonly NSButton _cancel;
    private readonly double _width;
    private readonly List<NSView> _fullWidth = [];
    private NSView? _firstResponder;
    private bool _presented;

    public FormSheet(string title, string? description, string confirmTitle, double width = 420)
    {
        _width = width;
        _sheet = new NSWindow(new CGRect(0, 0, width, 200), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        _sheet.ReleaseWhenClosed(false);

        var heading = WinoStyle.Label(title, WinoStyle.Heading);
        var message = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        message.Hidden = string.IsNullOrWhiteSpace(description);
        message.PreferredMaxLayoutWidth = (nfloat)(width - 40);

        _error = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);
        _error.Hidden = true;
        _error.PreferredMaxLayoutWidth = (nfloat)(width - 40);

        _cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        _confirm = new NSButton { Title = confirmTitle, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), _cancel, _confirm);

        _stack = WinoLayout.VStack(WinoStyle.Space3, heading, message, _grid, _error, buttons);
        _stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        _fullWidth.AddRange([message, _error, buttons]);
        _sheet.Title = title;
    }

    /// <summary>Returns an error message to keep the sheet open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    /// <summary>Enables the primary button; re-evaluated after every field edit.</summary>
    public Func<bool>? CanConfirm { get; set; }

    public NSWindow Window => _sheet;

    public NSTextField AddTextField(string label, string? value, string? placeholder = null, bool secure = false)
    {
        NSTextField field = secure ? new NSSecureTextField() : new NSTextField();
        field.StringValue = value ?? string.Empty;
        field.PlaceholderString = placeholder ?? string.Empty;
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        field.WidthAnchor.ConstraintGreaterThanOrEqualTo(240).Active = true;
        WinoAccessibility.Label(field, label);
        field.Changed += (_, _) => Refresh();
        AddRow(label, field);
        _firstResponder ??= field;
        return field;
    }

    public NSPopUpButton AddPopUp(string label, IEnumerable<string> items, int selectedIndex, IReadOnlyList<NSImage?>? images = null)
    {
        var popup = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        int index = 0;
        foreach (var item in items)
        {
            var menuItem = new NSMenuItem(item ?? string.Empty);
            if (images is not null && index < images.Count) menuItem.Image = images[index];
            popup.Menu!.AddItem(menuItem);
            index++;
        }
        popup.SelectItem(selectedIndex >= 0 && selectedIndex < popup.ItemCount ? selectedIndex : (popup.ItemCount > 0 ? 0 : -1));
        popup.WidthAnchor.ConstraintGreaterThanOrEqualTo(240).Active = true;
        WinoAccessibility.Label(popup, label);
        popup.Activated += (_, _) => Refresh();
        AddRow(label, popup);
        return popup;
    }

    /// <summary>A labelled row; a null label spans the control across both columns.</summary>
    public void AddRow(string? label, NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        if (label is null)
        {
            _grid.AddRow([NSGridCell.EmptyContentView, view]);
            return;
        }
        var caption = WinoStyle.Label(label, WinoStyle.Body, WinoStyle.SecondaryText);
        caption.Alignment = NSTextAlignment.Right;
        _grid.AddRow([caption, view]);
    }

    /// <summary>A view under the grid that spans the sheet width (a hint, a preview).</summary>
    public void AddFullWidth(NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _stack.InsertArrangedSubview(view, (nint)Array.IndexOf(_stack.ArrangedSubviews, _error));
        _fullWidth.Add(view);
    }

    public void ShowError(string? message)
    {
        _error.StringValue = message ?? string.Empty;
        _error.Hidden = string.IsNullOrEmpty(message);
    }

    public void Refresh() => _confirm.Enabled = CanConfirm?.Invoke() ?? true;

    public Task<bool> PresentAsync(NSWindow parent)
    {
        if (_presented) throw new InvalidOperationException("A form sheet is presented once.");
        _presented = true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish(bool confirmed)
        {
            if (!completion.TrySetResult(confirmed)) return;
            parent.EndSheet(_sheet);
        }

        _cancel.Activated += (_, _) => Finish(false);
        _confirm.Activated += (_, _) =>
        {
            if (CanConfirm is not null && !CanConfirm()) return;
            var problem = Validate?.Invoke();
            ShowError(problem);
            if (problem is null) Finish(true);
        };

        foreach (var view in _fullWidth) view.WidthAnchor.ConstraintEqualTo(_stack.WidthAnchor, 1, -40).Active = true;
        _stack.WidthAnchor.ConstraintEqualTo((nfloat)_width).Active = true;
        WinoLayout.Fill(_stack, _sheet.ContentView!);
        _sheet.SetContentSize(_stack.FittingSize);
        if (_firstResponder is not null) _sheet.InitialFirstResponder = _firstResponder;
        Refresh();

        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Finish(false), parent);
        parent.BeginSheet(_sheet, _ =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); closing = null; }
            completion.TrySetResult(false);
            _sheet.Dispose();
        });
        return completion.Task;
    }
}
