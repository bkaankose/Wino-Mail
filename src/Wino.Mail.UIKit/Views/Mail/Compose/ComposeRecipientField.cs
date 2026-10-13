using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// One To / Cc / Bcc row (the Mac NSTokenField): the caption, the recipients as <see cref="WinoChipView"/> tokens in
/// a <see cref="WinoFlowView"/> followed by an inline text field, and an optional trailing view (the Cc/Bcc reveal).
/// Typing a comma or semicolon, or Return, asks the page to commit the typed text. Backspace in the empty field selects
/// the last token, a second Backspace removes it (Apple Mail). A token's context menu copies its address or removes it.
/// The field shows what the page sets with <see cref="SetContacts"/>; the ViewModel collections stay the source of truth.
/// </summary>
internal sealed class ComposeRecipientField : UIView
{
    private static readonly char[] Delimiters = [',', ';'];
    private readonly WinoFlowView _flow = new() { Spacing = 6, LineSpacing = 6 };
    private readonly List<(WinoChipView Chip, AccountContact Contact)> _tokens = [];
    private WinoChipView? _selected;

    public ComposeRecipientField(string caption, UIView? trailing = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Caption = caption;
        TextField = new RecipientTextField(this) { AccessibilityLabel = caption };
        TextField.EditingChanged += (_, _) =>
        {
            ClearSelection();
            TextField.UpdateWidth(_flow);
            // An input method's marked text is still being composed.
            if (TextField.MarkedTextRange is null) TextChanged?.Invoke(this, EventArgs.Empty);
        };
        TextField.EditingDidBegin += (_, _) => EditingBegan?.Invoke(this, EventArgs.Empty);
        TextField.EditingDidEnd += (_, _) => { ClearSelection(); EditingEnded?.Invoke(this, EventArgs.Empty); };
        TextField.ShouldReturn = _ =>
        {
            ReturnPressed?.Invoke(this, EventArgs.Empty);
            return false;
        };
        TextField.ShouldChangeCharacters = (field, range, replacement) =>
        {
            if (string.IsNullOrEmpty(replacement) || replacement.IndexOfAny(Delimiters) < 0) return true;
            var current = field.Text ?? string.Empty;
            var start = (int)Math.Clamp(range.Location, 0, current.Length);
            var length = (int)Math.Clamp(range.Length, 0, current.Length - start);
            var text = current[..start] + replacement + current[(start + length)..];
            var parts = text.Split(Delimiters);
            field.Text = parts[^1].TrimStart();
            foreach (var part in parts[..^1])
                if (!string.IsNullOrWhiteSpace(part)) CommitRequested?.Invoke(this, part.Trim());
            TextField.UpdateWidth(_flow);
            return false;
        };

        var label = WinoStyle.Label(caption, WinoStyle.Body, WinoStyle.SecondaryText);
        label.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        label.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        label.IsAccessibilityElement = false;
        _flow.AddItem(TextField);
        _flow.ItemsChanged();
        _flow.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _flow.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Horizontal);
        _flow.HeightAnchor.ConstraintGreaterThanOrEqualTo(32).Active = true;
        _flow.AddGestureRecognizer(new UITapGestureRecognizer(() => TextField.BecomeFirstResponder()));

        var views = new List<UIView> { label, _flow };
        if (trailing is not null) views.Add(trailing);
        var row = WinoLayout.HStack(8, views.ToArray());
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Insets(row, 6, 16, 6, 12);
        var separator = new WinoSeparator();
        var stack = WinoLayout.VStack(0, row, separator);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        label.UserInteractionEnabled = true;
        label.AddGestureRecognizer(new UITapGestureRecognizer(() => TextField.BecomeFirstResponder()));
    }

    public string Caption { get; }

    public RecipientTextField TextField { get; }

    /// <summary>The text typed after the last token, trimmed.</summary>
    public string TypedText => (TextField.Text ?? string.Empty).Trim();

    public bool HasMarkedText => TextField.MarkedTextRange is not null;

    public IReadOnlyList<AccountContact> Contacts => _tokens.Select(static token => token.Contact).ToList();

    /// <summary>Typed text changed (never while an input method composes).</summary>
    public event EventHandler? TextChanged;

    /// <summary>A delimiter was typed or pasted: the text before it should become a recipient.</summary>
    public event EventHandler<string>? CommitRequested;

    public event EventHandler? ReturnPressed;
    public event EventHandler? EditingBegan;
    public event EventHandler? EditingEnded;

    /// <summary>A token's Remove (context menu, VoiceOver action or a second Backspace).</summary>
    public event EventHandler<AccountContact>? RemoveRequested;

    public void ClearTypedText()
    {
        TextField.Text = string.Empty;
        TextField.UpdateWidth(_flow);
    }

    public void Focus() => TextField.BecomeFirstResponder();

    /// <summary>Shows <paramref name="contacts"/> as tokens before the text field.</summary>
    public void SetContacts(IEnumerable<AccountContact> contacts)
    {
        var list = contacts.Where(static contact => !string.IsNullOrWhiteSpace(contact.Address)).ToList();
        if (list.Count == _tokens.Count && list.Select(static contact => contact.Address).SequenceEqual(_tokens.Select(static token => token.Contact.Address), StringComparer.OrdinalIgnoreCase))
            return;
        _selected = null;
        foreach (var (chip, _) in _tokens) chip.RemoveFromSuperview();
        _tokens.Clear();
        // The text field stays in the flow (removing it would end editing and drop the keyboard); tokens go before it.
        var index = 0;
        foreach (var contact in list)
        {
            var chip = CreateChip(contact);
            _tokens.Add((chip, contact));
            chip.TranslatesAutoresizingMaskIntoConstraints = true;
            _flow.InsertSubview(chip, index++);
        }
        _flow.ItemsChanged();
        TextField.UpdateWidth(_flow);
    }

    private WinoChipView CreateChip(AccountContact contact)
    {
        var name = string.IsNullOrWhiteSpace(contact.Name) ? contact.Address : contact.Name;
        var chip = new WinoChipView(28)
        {
            Text = name,
            TextColor = WinoStyle.PrimaryText,
            Fill = WinoStyle.SubtleFill,
            MaxTextWidth = 220,
            IsClickable = true
        };
        chip.AccessibilityLabel = string.Equals(name, contact.Address, StringComparison.OrdinalIgnoreCase) ? name : $"{name}, {contact.Address}";
        chip.AccessibilityHint = contact.Address;
        chip.Clicked += (_, _) => Select(chip);
        chip.AccessibilityCustomActions =
        [
            new UIAccessibilityCustomAction(Translator.Buttons_Remove, (Func<UIAccessibilityCustomAction, bool>)(_ => { RemoveRequested?.Invoke(this, contact); return true; })),
            new UIAccessibilityCustomAction(Translator.Buttons_Copy, (Func<UIAccessibilityCustomAction, bool>)(_ => { UIPasteboard.General.String = contact.Address; return true; }))
        ];
        chip.AddInteraction(new UIContextMenuInteraction(new TokenMenu(this, contact)));
        return chip;
    }

    /// <summary>Selects a token so Backspace removes it; the caret stays in the field.</summary>
    private void Select(WinoChipView chip)
    {
        var previous = _selected;
        ClearSelection();
        if (ReferenceEquals(previous, chip)) return;
        _selected = chip;
        chip.Fill = WinoStyle.Accent;
        chip.TextColor = UIColor.White;
        chip.AccessibilityTraits |= UIAccessibilityTrait.Selected;
        if (!TextField.IsFirstResponder) TextField.BecomeFirstResponder();
    }

    private void ClearSelection()
    {
        if (_selected is not { } chip) return;
        _selected = null;
        chip.Fill = WinoStyle.SubtleFill;
        chip.TextColor = WinoStyle.PrimaryText;
        chip.AccessibilityTraits &= ~UIAccessibilityTrait.Selected;
    }

    /// <summary>Backspace with no typed text: the first selects the last token, the next removes the selected one.</summary>
    private void BackspaceOnEmpty()
    {
        if (_selected is { } selected)
        {
            var token = _tokens.FirstOrDefault(item => ReferenceEquals(item.Chip, selected));
            ClearSelection();
            if (token.Contact is not null) RemoveRequested?.Invoke(this, token.Contact);
            return;
        }
        if (_tokens.Count > 0) Select(_tokens[^1].Chip);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            TextChanged = null;
            CommitRequested = null;
            ReturnPressed = null;
            EditingBegan = null;
            EditingEnded = null;
            RemoveRequested = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>The inline field: e-mail keyboard, no autocorrection, and a width that follows its text.</summary>
    internal sealed class RecipientTextField : UITextField
    {
        private const double MinimumWidth = 120;
        private readonly ComposeRecipientField _owner;
        private nfloat _width = (nfloat)MinimumWidth;

        public RecipientTextField(ComposeRecipientField owner)
        {
            _owner = owner;
            Font = WinoStyle.Body;
            AdjustsFontForContentSizeCategory = true;
            KeyboardType = UIKeyboardType.EmailAddress;
            AutocorrectionType = UITextAutocorrectionType.No;
            AutocapitalizationType = UITextAutocapitalizationType.None;
            SpellCheckingType = UITextSpellCheckingType.No;
            TextContentType = UITextContentType.EmailAddress;
            ReturnKeyType = UIReturnKeyType.Next;
        }

        public override CGSize IntrinsicContentSize => new(_width, 32);

        /// <summary>Grows with the typed text (at least 120pt) and re-flows the row when the width changed.</summary>
        public void UpdateWidth(WinoFlowView flow)
        {
            var text = new NSString(Text ?? string.Empty);
            var measured = text.GetSizeUsingAttributes(new UIStringAttributes { Font = Font ?? WinoStyle.Body }).Width + 24;
            var available = flow.Bounds.Width > 0 ? flow.Bounds.Width : (nfloat)MinimumWidth;
            var width = (nfloat)Math.Min(Math.Max(MinimumWidth, Math.Ceiling(measured)), Math.Max(MinimumWidth, available));
            if (width == _width) return;
            _width = width;
            InvalidateIntrinsicContentSize();
            flow.ItemsChanged();
        }

        public override void DeleteBackward()
        {
            if (string.IsNullOrEmpty(Text) && MarkedTextRange is null)
            {
                _owner.BackspaceOnEmpty();
                return;
            }
            base.DeleteBackward();
        }
    }

    /// <summary>The token context menu: Copy (Windows ContactTokenContextRequested) and Remove.</summary>
    private sealed class TokenMenu(ComposeRecipientField owner, AccountContact contact) : UIContextMenuInteractionDelegate
    {
        public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
            => UIContextMenuConfiguration.Create(null, null, _ => UIMenu.Create(contact.Address ?? string.Empty,
            [
                UIAction.Create(Translator.Buttons_Copy, WinoIcons.Image(WinoIconGlyph.Copy, 16), null, _ => UIPasteboard.General.String = contact.Address ?? string.Empty),
                DestructiveRemove()
            ]));

        private UIAction DestructiveRemove()
        {
            var remove = UIAction.Create(Translator.Buttons_Remove, WinoIcons.Image(WinoIconGlyph.Dismiss, 16), null, _ => owner.RemoveRequested?.Invoke(owner, contact));
            remove.Attributes = UIMenuElementAttributes.Destructive;
            return remove;
        }
    }
}
