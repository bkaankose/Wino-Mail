using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Storage: the local MIME cache summary with a scan button, then one expander per account (account icon, name, usage)
/// with "delete all" and "delete older than" actions (Windows StoragePage, iOS StoragePageViewController).
/// </summary>
public sealed class StoragePageFragment : SettingsPageFragment<StoragePageViewModel>
{
    private WinoSettingsGroup? _accounts;
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = VM;
        var summary = Bind.Label(vm, nameof(vm.SummaryText), s => s.SummaryText, maximumLines: 0);
        var scan = Bind.Button(Translator.SettingsStorage_ScanFolder, vm.RefreshStorageCommand, primary: true);
        Bind.Enabled(scan, vm, nameof(vm.IsBusy), s => !s.IsBusy);
        var hero = Card(Translator.SettingsStorage_Title, Translator.SettingsStorage_Description, WinoIconGlyph.Storage, scan);
        hero.BottomContent = summary;
        AddGroup(null, hero);

        _accounts = Add(new WinoSettingsGroup(Ctx));
        Bindings.Own(new Wino.Mail.Android.Infrastructure.ActionDisposable(() => _rowScope?.Dispose()));
        Bind.Collection(vm.AccountStorageItems, RebuildAccounts);
    }

    private void RebuildAccounts()
    {
        if (_accounts is not { } accounts) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        accounts.Clear();
        var pictures = MainApplication.Services?.GetService<IPictureStorageService>();

        foreach (var item in VM.AccountStorageItems.ToList())
        {
            var expander = new WinoSettingsExpander(Ctx, item.AccountName, item.SizeDescription);
            var icon = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_mail_account_icon), DisplayName = item.AccountName };
            if (pictures is not null) icon.Account = MailAccountIconInfoFactory.Create(item.Account, pictures);
            expander.HeaderCard.LeadingView = icon;
            rows.Bind(item, nameof(item.SizeDescription), i => i.SizeDescription, text => expander.HeaderCard.Description = text);

            var deleteAll = rows.Button(Translator.SettingsStorage_DeleteAll_Button, item.DeleteAllCommand, () => item);
            expander.Add(rows.Enabled(new WinoSettingsCard(Ctx, Translator.SettingsStorage_DeleteAll_Title, Translator.SettingsStorage_DeleteAll_Description, WinoIconGlyph.None, deleteAll),
                item, nameof(item.IsBusy), i => !i.IsBusy));

            // Four period buttons do not fit beside the text on a phone: a 2×2 grid under it.
            var older = Stack(Resource.Dimension.wino_space_s,
                EqualRow(rows.Button(Translator.SettingsStorage_DeleteOld_1Month, item.DeleteOneMonthCommand, () => item),
                         rows.Button(Translator.SettingsStorage_DeleteOld_3Months, item.DeleteThreeMonthsCommand, () => item)),
                EqualRow(rows.Button(Translator.SettingsStorage_DeleteOld_6Months, item.DeleteSixMonthsCommand, () => item),
                         rows.Button(Translator.SettingsStorage_DeleteOld_1Year, item.DeleteYearCommand, () => item)));
            var olderCard = new WinoSettingsCard(Ctx, Translator.SettingsStorage_DeleteOld_Title, Translator.SettingsStorage_DeleteOld_Description);
            older.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            olderCard.BottomContent = older;
            expander.Add(rows.Enabled(olderCard, item, nameof(item.IsBusy), i => !i.IsBusy));
            accounts.Add(expander);
        }
        accounts.Visibility = accounts.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private LinearLayout EqualRow(View first, View second)
    {
        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.AddView(first, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(second, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        return row;
    }
}
