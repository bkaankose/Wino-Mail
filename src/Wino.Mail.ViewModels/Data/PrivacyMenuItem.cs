#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One action in the reader's privacy flyout. Every head renders the same list, in order, and invokes
/// <c>MailRenderingPageViewModel.InvokePrivacyMenuItemCommand</c> with the item.
/// </summary>
/// <param name="Text">The localized label.</param>
/// <param name="Hint">A secondary caption shown after the label, like "until Wino closes", or null.</param>
/// <param name="StartsGroup">The head draws a separator before this item.</param>
/// <param name="IsRecommended">The single most likely action; the head may give it accent styling.</param>
public sealed record PrivacyMenuItem(
    PrivacyMenuAction Action,
    string Text,
    string? Hint,
    WinoIconGlyph Icon,
    bool StartsGroup = false,
    bool IsRecommended = false)
{
    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

/// <summary>
/// A host the reader blocked, grouped by reason, for the privacy flyout's list.
/// </summary>
/// <param name="Description">The localized reason, with the count when there is more than one, like "known tracker · 2".</param>
public sealed record PrivacyBlockedHost(string Host, string Description, int Count);
