using AppKit;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// The quick add card at the bottom of the task list (Windows ToDoPage composer): Add glyph in
/// the accent, a borderless field and the accent "Add task" button. Return submits; the owner
/// binds <see cref="Text"/> both ways and runs the add command on <see cref="Submitted"/>.
/// </summary>
public sealed class WinoQuickAddView : NSView
{
    private readonly WinoSurfaceView _card = WinoToDoStyle.Card();
    private readonly WinoIconView _icon = new(WinoIconGlyph.Add, 16, WinoStyle.Accent);
    private readonly NSTextField _field = new();
    private readonly NSButton _button;
    private bool _focused;

    public WinoQuickAddView(string addTitle)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_card, this);

        _field.Bezeled = false;
        _field.Bordered = false;
        _field.DrawsBackground = false;
        _field.FocusRingType = NSFocusRingType.None;
        _field.Font = WinoStyle.Body;
        _field.TranslatesAutoresizingMaskIntoConstraints = false;
        _field.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _field.UsesSingleLineMode = true;
        _field.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _field.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _field.Changed += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
        _field.EditingBegan += (_, _) => { _focused = true; FocusChanged?.Invoke(this, EventArgs.Empty); };
        _field.EditingEnded += (_, _) => { _focused = false; FocusChanged?.Invoke(this, EventArgs.Empty); };
        // Return sends the field's action; the owner runs the add command.
        _field.Activated += (_, _) => { if (!string.IsNullOrWhiteSpace(_field.StringValue)) Submitted?.Invoke(this, EventArgs.Empty); };

        _button = WinoToDoStyle.AccentButton(addTitle);
        _button.Activated += (_, _) => Submitted?.Invoke(this, EventArgs.Empty);

        var row = WinoLayout.HStack(10, _icon, _field, _button);
        row.EdgeInsets = new NSEdgeInsets(8, 12, 8, 12);
        WinoLayout.Fill(row, _card);
        WinoStyle.AccentChanged += AccentChanged;
    }

    public event EventHandler? Submitted;
    public event EventHandler? TextChanged;
    public event EventHandler? FocusChanged;

    public bool IsFocused => _focused;

    public string Text
    {
        get => _field.StringValue;
        set { if (_field.StringValue != value) _field.StringValue = value ?? string.Empty; }
    }

    public string Placeholder
    {
        set
        {
            _field.PlaceholderString = value;
            WinoAccessibility.Label(_field, value);
        }
    }

    public bool Enabled
    {
        set { _field.Enabled = value; _button.Enabled = value; }
    }

    /// <summary>Hides the Add button until the composer is in use (Windows IsComposerExpanded).</summary>
    public bool ShowButton
    {
        set => _button.Hidden = !value;
    }

    public void Focus() => Window?.MakeFirstResponder(_field);

    private void AccentChanged(object? sender, EventArgs e)
    {
        _icon.Tint = WinoStyle.Accent;
        WinoToDoStyle.ApplyAccent(_button);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}
