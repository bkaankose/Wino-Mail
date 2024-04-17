namespace Wino.Core.Messages.Accounts
{
    /// <summary>
    /// When a full menu refresh for accounts menu is requested.
    /// </summary>
    public record AccountsMenuRefreshRequested(bool AutomaticallyNavigateFirstItem = true);
}
