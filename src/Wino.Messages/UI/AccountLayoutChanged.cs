namespace Wino.Messaging.UI;

/// <summary>
/// Account groups, their members or the account order changed. Every mode pane rebuilds its account area.
/// </summary>
public record AccountLayoutChanged : UIMessageBase<AccountLayoutChanged>;
