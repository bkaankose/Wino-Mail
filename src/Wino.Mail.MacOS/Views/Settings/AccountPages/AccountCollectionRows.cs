using System.Collections.Generic;
using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>Fills a section with the address books or task lists of an account, keeping its empty-state row.</summary>
internal static class AccountCollectionRows
{
    public static void Fill(WinoSettingsGroup group, NSView placeholder, IEnumerable<AccountCollectionItem> items, WinoIconGlyph icon)
    {
        group.Clear();
        group.Add(placeholder);
        foreach (var item in items)
            group.Add(new WinoSettingsCard(item.Title, item.Detail, icon));
    }
}
