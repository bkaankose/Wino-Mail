using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>Fills a section with the address books or task lists of an account, keeping its empty-state row first (macOS AccountCollectionRows).</summary>
internal static class AccountCollectionRows
{
    public static void Fill(WinoSettingsGroup group, Adw.PreferencesRow placeholder, AccountCollectionItem[] items, WinoIconGlyph glyph)
    {
        group.Clear();
        group.Add(placeholder);
        foreach (var item in items) group.Add(WinoSettingsRows.Action(item.Title, item.Detail, glyph));
    }
}
