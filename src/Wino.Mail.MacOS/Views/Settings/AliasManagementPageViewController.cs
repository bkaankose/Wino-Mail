using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Aliases of one account (Manage accounts › account › Aliases): Add and Sync buttons, the summary
/// line, then one card per alias with its source, reply-to and send status, Set primary and Delete
/// (Windows AliasManagementPage). The reply-to and S/MIME editors are not on Mac yet.
/// </summary>
public sealed class AliasManagementPageViewController(AliasManagementPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AliasManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _aliases = new();
    private BindingScope? _rowScope;

    public string? PageTitle => Translator.SettingsManageAliases_Title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var add = Bind.Button(Translator.Buttons_AddNewAlias, vm.AddNewAliasCommand, primary: true, icon: WinoIconGlyph.Add);
        var sync = Bind.Button(Translator.Buttons_SyncAliases, vm.SyncAliasesCommand, icon: WinoIconGlyph.Sync);
        Bind.Visible(sync, vm, nameof(vm.CanSynchronizeAliases), s => s.CanSynchronizeAliases);
        var syncing = Caption(Translator.AccountAlias_Synchronizing);
        Bind.Visible(syncing, vm, nameof(vm.IsSynchronizing), s => s.IsSynchronizing);
        var buttons = Row(add, sync, syncing);
        buttons.EdgeInsets = new NSEdgeInsets(0, 0, 8, 0);
        Add(buttons);

        // Both Windows InfoBars on this page are IsClosable="True".
        var info = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountAlias_Info_Title, Translator.AccountAlias_Info_Message);
        info.IsClosable = true;
        Add(info);

        var error = InfoBar(WinoInfoBarSeverity.Error, Translator.AccountAlias_SyncErrorTitle, null);
        error.IsClosable = true;
        Bind.Bind(vm, nameof(vm.SynchronizationError), s => s.SynchronizationError, text => { error.Message = text; error.Hidden = string.IsNullOrEmpty(text); });
        Add(error);

        var summary = Bind.Label(vm, nameof(vm.AliasSummary), s => s.AliasSummary, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        Bind.Visible(Add(summary), vm, nameof(vm.HasAliases), s => s.HasAliases);

        var loading = Caption(Translator.AccountAlias_Loading);
        Bind.Visible(Add(loading), vm, nameof(vm.IsLoading), s => s.IsLoading);

        var empty = Card(Translator.AccountAlias_Empty_Title, Translator.AccountAlias_Empty_Description, WinoIconGlyph.Mail);
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmptyStateVisible), s => s.IsEmptyStateVisible);

        Add(_aliases);
        Bind.Bind(vm, nameof(vm.AccountAliases), s => s.AliasItems, Rebuild);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    private void Rebuild(IReadOnlyList<AliasManagementItem>? items)
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _aliases.Clear();

        foreach (var item in items ?? [])
        {
            var header = item.IsPrimary ? $"{item.AliasAddress}  ·  {Translator.AccountAlias_PrimaryBadge}" : item.AliasAddress;
            var description = item.HasStatusDetail ? $"{item.DescriptionText}\n{item.StatusDetailText}" : item.DescriptionText;
            var status = Caption(item.StatusText);
            status.TextColor = item.IsCapabilityDenied ? WinoStyle.Critical : item.IsCapabilityUnknown ? WinoStyle.Caution : WinoStyle.SecondaryText;
            var controls = Row(status);
            if (item.CanSetPrimary) controls.AddArrangedSubview(rows.Button(Translator.AccountAlias_SetPrimaryAction, ViewModel.SetAliasPrimaryCommand, () => item.Alias, icon: WinoIconGlyph.Star));
            if (item.CanDelete)
            {
                var delete = rows.Button(string.Empty, ViewModel.DeleteAliasCommand, () => item.Alias, icon: WinoIconGlyph.Delete);
                delete.ToolTip = Translator.AccountAlias_DeleteAction;
                controls.AddArrangedSubview(delete);
            }
            var card = new WinoSettingsCard(header, description, item.IsPrimary ? WinoIconGlyph.Star : WinoIconGlyph.Mail, controls);
            _aliases.Add(card);
        }
        _aliases.Hidden = _aliases.RowCount == 0;
    }
}
