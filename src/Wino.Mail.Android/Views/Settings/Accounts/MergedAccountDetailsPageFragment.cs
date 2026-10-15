using System.Collections.ObjectModel;
using System.Windows.Input;
using Android.Views;
using Android.Views.InputMethods;
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
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Linked inbox details (iOS MergedAccountDetailsPageViewController, Windows MergedAccountDetailsPage): the name edited
/// in place, the mail-only notice, the Manage link expander with Linked and Available accounts stacked (Link / Unlink
/// buttons move a row to the other list; rows never reorder), the two-account rule, Save changes, and Unlink accounts
/// under Remove. The title follows the linked inbox name.
/// </summary>
public sealed class MergedAccountDetailsPageFragment : SettingsPageFragment<MergedAccountDetailsPageViewModel>, ISettingsPageTitleSource
{
    private IPictureStorageService? _pictures;
    private readonly List<BindingScope> _listScopes = [];

    public string? PageTitle => ViewModel is not { } vm ? null
        : vm.EditingMergedAccount?.MergedInbox?.Name is { Length: > 0 } name ? name : vm.MergedAccountName;

    protected override void BuildPage()
    {
        var vm = VM;
        _pictures = MainApplication.Services?.GetService<IPictureStorageService>();
        Bindings.Own(new ActionDisposable(() =>
        {
            foreach (var scope in _listScopes) scope.Dispose();
            _listScopes.Clear();
        }));
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, _ => UpdateTitle());
        Bind.Bind(vm, nameof(vm.EditingMergedAccount), s => s.EditingMergedAccount, _ => UpdateTitle());

        Add(StackedCard(Translator.SettingsRenameMergeAccount_Title, Translator.SettingsRenameMergeAccount_Description, WinoIconGlyph.Rename, NameField()));

        Add(InfoBar(WinoInfoBarSeverity.Informational, null, Translator.MergedAccount_MailOnlyMessage));

        var manage = Expander(Translator.SettingsManageLink_Title, Translator.SettingsManageLink_Description, WinoIconGlyph.PeopleLink, null);
        manage.Add(Columns(), WinoTokens.Dp(Ctx, Resource.Dimension.wino_space_m), 0, WinoTokens.Dp(Ctx, Resource.Dimension.wino_space_ml), 0);
        manage.IsExpanded = true;
        Add(manage);

        var save = Bind.Button(Translator.SettingsLinkedAccountsSave_Title, vm.SaveChangesCommand, primary: true);
        Add(StackedCard(Translator.SettingsLinkedAccountsSave_Title, Translator.SettingsLinkedAccountsSave_Description, WinoIconGlyph.Save, Row(save)));

        var unlink = AccountSettingsParts.Destructive(Ctx, Bind.Button(Translator.MergedAccount_Unlink + "…", vm.UnlinkAccountsCommand));
        AddGroup(Translator.Buttons_Remove,
            StackedCard(Translator.SettingsUnlinkAccounts_Title, Translator.SettingsUnlinkAccounts_Description, WinoIconGlyph.SubtractCircle, Row(unlink)));
    }

    /// <summary>The inline name field: shows the current name and renames on Done or when the field loses focus.</summary>
    private View NameField()
    {
        var vm = VM;
        var layout = SettingsBinder.CreateTextField(Ctx, Translator.SettingsRenameMergeAccount_Title);
        layout.ContentDescription = Translator.SettingsRenameMergeAccount_Title;
        if (layout.EditText is not { } field) return layout;
        field.ImeOptions = ImeAction.Done;
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, name =>
        {
            if (!field.HasFocus && field.Text != (name ?? string.Empty)) field.Text = name ?? string.Empty;
        });

        void Commit()
        {
            var name = field.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                field.Text = vm.MergedAccountName ?? string.Empty;
                return;
            }
            if (name == vm.MergedAccountName) return;
            AccountSettingsParts.Run(vm.ApplyMergedAccountNameAsync(name), ReportError);
        }

        EventHandler<View.FocusChangeEventArgs> focus = (_, args) => { if (!args.HasFocus) Commit(); };
        EventHandler<TextView.EditorActionEventArgs> done = (_, args) =>
        {
            Commit();
            field.ClearFocus();
            args.Handled = false;
        };
        field.FocusChange += focus;
        field.EditorAction += done;
        Bindings.Own(new ActionDisposable(() =>
        {
            field.FocusChange -= focus;
            field.EditorAction -= done;
        }));
        return layout;
    }

    /// <summary>Linked accounts, then Available accounts, each with its header (side by side on the Mac).</summary>
    private View Columns()
    {
        var vm = VM;
        var policy = AccountSettingsParts.Label(Ctx, Translator.LinkedAccountsCreatePolicyMessage, WinoTextStyle.Description, WinoColors.Caution(Ctx));
        Bind.Visible(policy, vm, nameof(vm.ShouldDeleteMergedAccount), s => s.ShouldDeleteMergedAccount);

        var linkedRows = AccountSettingsParts.VColumn(Ctx);
        var linkedEmpty = DashedZone(null);
        var linkedCount = Caption(string.Empty);
        var top = Column(Translator.LinkedAccountsTitle, linkedCount, policy, linkedRows, linkedEmpty);
        BindList(vm.LinkedAccounts, linkedRows, linked: true, () =>
        {
            linkedCount.Text = vm.LinkedAccounts.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            linkedEmpty.Visibility = vm.LinkedAccounts.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        });

        var availableRows = AccountSettingsParts.VColumn(Ctx);
        var availableCount = Caption(string.Empty);
        var bottom = Column(Translator.MergedAccountsAvailableAccountsTitle, availableCount, availableRows, DashedZone(Translator.MergedAccount_AvailableDropHint));
        BindList(vm.UnlinkedAccounts, availableRows, linked: false,
            () => availableCount.Text = vm.UnlinkedAccounts.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));

        return Stack(Resource.Dimension.wino_space_2xl, top, bottom);
    }

    private void BindList(ObservableCollection<AccountProviderDetailViewModel> items, LinearLayout host, bool linked, Action changed)
    {
        BindingScope? scope = null;
        Bind.Collection(items, () =>
        {
            if (scope is not null)
            {
                scope.Dispose();
                _listScopes.Remove(scope);
            }
            scope = new BindingScope();
            _listScopes.Add(scope);
            var rows = Bind.Child(scope);
            host.RemoveAllViews();
            foreach (var account in items.ToList()) AccountSettingsParts.AddStretched(host, AccountRow(rows, account, linked), Resource.Dimension.wino_space_xs);
            host.Visibility = host.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
            changed();
        });
    }

    private LinearLayout Column(string title, TextView count, params View[] views)
    {
        var heading = AccountSettingsParts.Label(Ctx, title, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) heading.AccessibilityHeading = true;
        var header = AccountSettingsParts.HRow(Ctx);
        header.AddView(heading);
        header.AddView(count, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_s));
        var column = Stack(Resource.Dimension.wino_space_xs, header);
        foreach (var view in views) AddTo(column, view, Resource.Dimension.wino_space_xs);
        return column;
    }

    /// <summary>The dashed placeholder (empty linked list, the available list's hint).</summary>
    private TextView DashedZone(string? text)
    {
        var label = AccountSettingsParts.Label(Ctx, text, WinoTextStyle.Caption, WinoColors.TertiaryText(Ctx), maxLines: 2);
        label.Gravity = GravityFlags.Center;
        label.SetMinHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_row));
        WinoTokens.Pad(label, Resource.Dimension.wino_space_m, 0, Resource.Dimension.wino_space_m, 0);
        label.Background = AccountSettingsParts.DashedOutline(Ctx);
        return label;
    }

    /// <summary>A raised account row: account picture or provider glyph, name and address, then Unlink or Link.</summary>
    private View AccountRow(SettingsBinder rows, AccountProviderDetailViewModel account, bool linked)
    {
        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_row));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_2xs);
        row.Background = AccountSettingsParts.Surface(Ctx, AccountSettingsParts.RowFill(Ctx), WinoColors.CardStroke(Ctx));

        var picture = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_size_swatch), DisplayName = account.Account?.Name };
        if (account.Account is { } mailAccount && _pictures is not null) picture.Account = MailAccountIconInfoFactory.Create(mailAccount, _pictures);
        row.AddView(picture);

        var accountName = account.Account?.Name ?? string.Empty;
        var accountAddress = account.Account?.Address;
        var text = AccountSettingsParts.VColumn(Ctx);
        text.AddView(AccountSettingsParts.Label(Ctx, accountName, WinoTextStyle.Body, maxLines: 1));
        text.AddView(AccountSettingsParts.Label(Ctx, accountAddress, WinoTextStyle.Caption, maxLines: 1));
        row.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_sm));

        ICommand command = linked ? VM.UnlinkAccountCommand : VM.LinkAccountCommand;
        var title = linked ? Translator.MergedAccount_Unlink : Translator.Link;
        var action = AccountSettingsParts.PillButton(Ctx, title, linked ? WinoIconGlyph.None : WinoIconGlyph.Add);
        action.ContentDescription = $"{title} {accountName}";
        rows.Views.Click(action, () => AccountSettingsParts.Execute(command, account, ReportError));
        row.AddView(action, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_sm));

        row.ContentDescription = string.IsNullOrEmpty(accountAddress) ? accountName : $"{accountName}, {accountAddress}";
        return row;
    }
}
