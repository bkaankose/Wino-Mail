namespace Wino.Messaging.UI;

/// <summary>
/// A remote content sender rule was added, changed or removed.
/// </summary>
/// <param name="Pattern">The changed rule's pattern, or null when several rules changed at once.</param>
public record RemoteContentRulesChangedMessage(string? Pattern) : UIMessageBase<RemoteContentRulesChangedMessage>;
