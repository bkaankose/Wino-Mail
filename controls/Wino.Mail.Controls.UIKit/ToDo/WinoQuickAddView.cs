using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.ToDo;

/// <summary>
/// The quick add card at the bottom of the task list (Windows ToDoPage composer; UIKit twin of the AppKit
/// <c>WinoQuickAddView</c>): Add glyph in the accent, a borderless field and the accent "Add task" button. Return
/// submits; the owner binds <see cref="Text"/> both ways and runs the add command on <see cref="Submitted"/>.
/// </summary>
public sealed class WinoQuickAddView : UIView
{
    private readonly WinoSurfaceView _card = WinoToDoStyle.Card();
    private readonly WinoIconView _icon = new(WinoIconGlyph.Add, 18, WinoStyle.Accent);
    private readonly UITextField _field;
    private readonly UIButton _button;
    private bool _focused;

    public WinoQuickAddView(string addTitle)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_card, this);

        _field = new UITextField
        {
            BorderStyle = UITextBorderStyle.None,
            Font = WinoStyle.Body,
            ReturnKeyType = UIReturnKeyType.Done,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Priorities(_field, UILayoutConstraintAxis.Horizontal, 1, 250);
        _field.EditingChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
        _field.EditingDidBegin += (_, _) => { _focused = true; FocusChanged?.Invoke(this, EventArgs.Empty); };
        _field.EditingDidEnd += (_, _) => { _focused = false; FocusChanged?.Invoke(this, EventArgs.Empty); };
        // Return sends the field's primary action; the owner runs the add command.
        _field.PrimaryActionTriggered += (_, _) => { if (!string.IsNullOrWhiteSpace(_field.Text)) Submitted?.Invoke(this, EventArgs.Empty); };

        _button = WinoToDoStyle.AccentButton(addTitle);
        _button.PrimaryActionTriggered += (_, _) => Submitted?.Invoke(this, EventArgs.Empty);

        var row = WinoLayout.Insets(WinoLayout.HStack(10, _icon, _field, _button), 8, 12, 8, 12);
        WinoLayout.Fill(row, _card);
        HeightAnchor.ConstraintGreaterThanOrEqualTo(52).Active = true;
        WinoStyle.AccentChanged += AccentChanged;
    }

    public event EventHandler? Submitted;
    public event EventHandler? TextChanged;
    public event EventHandler? FocusChanged;

    public bool IsFocused => _focused;

    public string Text
    {
        get => _field.Text ?? string.Empty;
        set { if (_field.Text != value) _field.Text = value ?? string.Empty; }
    }

    public string Placeholder
    {
        set
        {
            _field.Placeholder = value;
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

    public void Focus() => _field.BecomeFirstResponder();

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
