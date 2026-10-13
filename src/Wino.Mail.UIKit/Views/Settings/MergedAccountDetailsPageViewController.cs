using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Linked inbox details (Mac MergedAccountDetailsPageViewController, Windows MergedAccountDetailsPage): the name
/// edited in place, the mail-only notice, the Manage link expander with Linked and Available accounts (stacked at phone
/// width; Link / Unlink buttons move a row to the other list), the two-account rule, Save changes, and Unlink accounts
/// under Remove. The title follows the linked inbox name.
/// </summary>
public sealed class MergedAccountDetailsPageViewController(MergedAccountDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MergedAccountDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string LinkedKey = "linked";
    private const string AvailableKey = "available";
    private const double RowHeight = 56;
    private const double RowSpacing = 6;

    private readonly SettingsDragGroup _group = new();

    public string? PageTitle => ViewModel.EditingMergedAccount?.MergedInbox?.Name is { Length: > 0 } name ? name : ViewModel.MergedAccountName;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.EditingMergedAccount), s => s.EditingMergedAccount, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        Add(StackedCard(Translator.SettingsRenameMergeAccount_Title, Translator.SettingsRenameMergeAccount_Description, WinoIconGlyph.Rename, NameField()));

        Add(InfoBar(WinoInfoBarSeverity.Informational, null, Translator.MergedAccount_MailOnlyMessage));

        var manage = Expander(Translator.SettingsManageLink_Title, Translator.SettingsManageLink_Description, WinoIconGlyph.PeopleLink, null);
        manage.Add(Columns(), 12, WinoSettingsStyle.CardPadding, 14, WinoSettingsStyle.CardPadding);
        manage.IsExpanded = true;
        Add(manage);

        var save = Bind.Button(Translator.SettingsLinkedAccountsSave_Title, vm.SaveChangesCommand, primary: true);
        save.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        Add(StackedCard(Translator.SettingsLinkedAccountsSave_Title, Translator.SettingsLinkedAccountsSave_Description, WinoIconGlyph.Save, Row(save, WinoLayout.Spacer())));

        var unlink = Bind.Button(Translator.MergedAccount_Unlink + "…", vm.UnlinkAccountsCommand);
        if (unlink.Configuration is { } configuration)
        {
            configuration.BaseForegroundColor = WinoStyle.Critical;
            unlink.Configuration = configuration;
        }
        AddGroup(Translator.Buttons_Remove,
            Card(Translator.SettingsUnlinkAccounts_Title, Translator.SettingsUnlinkAccounts_Description, WinoIconGlyph.SubtractCircle, unlink));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

    /// <summary>The inline name field: shows the current name and renames on Return or when editing ends.</summary>
    private UITextField NameField()
    {
        var vm = ViewModel;
        var field = new UITextField
        {
            Font = WinoStyle.Body,
            Placeholder = Translator.SettingsRenameMergeAccount_Title,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ReturnKeyType = UIReturnKeyType.Done,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(field, Translator.SettingsRenameMergeAccount_Title);
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, name =>
        {
            if (!field.IsEditing && field.Text != (name ?? string.Empty)) field.Text = name ?? string.Empty;
        });
        Bind.OnActivated(field, () => field.ResignFirstResponder(), UIControlEvent.EditingDidEndOnExit);
        Bind.OnActivated(field, () =>
        {
            var name = field.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                field.Text = vm.MergedAccountName ?? string.Empty;
                return;
            }
            if (name == vm.MergedAccountName) return;
            Run(vm.ApplyMergedAccountNameAsync(name));
        }, UIControlEvent.EditingDidEnd);
        return field;
    }

    /// <summary>Linked accounts, then Available accounts, each with its header (side by side on the Mac).</summary>
    private UIView Columns()
    {
        var vm = ViewModel;
        // Rows never reorder inside a list; the Link / Unlink buttons move them across.
        _group.CanDrop = (source, target) => !ReferenceEquals(source, target);
        _group.Dropped = (source, from, _, _) =>
        {
            if (source.Key == LinkedKey && from < vm.LinkedAccounts.Count)
                Execute(vm.UnlinkAccountCommand, vm.LinkedAccounts[from]);
            else if (source.Key == AvailableKey && from < vm.UnlinkedAccounts.Count)
                Execute(vm.LinkAccountCommand, vm.UnlinkedAccounts[from]);
        };

        var linkedCount = Caption(string.Empty);
        var policy = WinoStyle.Label(Translator.LinkedAccountsCreatePolicyMessage, WinoSettingsStyle.CardDescription, WinoStyle.Caution, 0);
        Bind.Visible(policy, vm, nameof(vm.ShouldDeleteMergedAccount), s => s.ShouldDeleteMergedAccount);
        var linked = new SettingsDragList<AccountProviderDetailViewModel>(LinkedKey, vm.LinkedAccounts, _group, (account, _) => AccountRow(account, linked: true), RowHeight, RowSpacing);
        linked.SetEmptyView(new DashedDropZoneView(null), RowHeight);
        Bindings.Own(linked.Observe(vm.LinkedAccounts, Dispatcher, () => linkedCount.Text = vm.LinkedAccounts.Count.ToString()));

        var availableCount = Caption(string.Empty);
        var available = new SettingsDragList<AccountProviderDetailViewModel>(AvailableKey, vm.UnlinkedAccounts, _group, (account, _) => AccountRow(account, linked: false), RowHeight, RowSpacing);
        available.SetFooterView(new DashedDropZoneView(Translator.MergedAccount_AvailableDropHint), RowHeight);
        Bindings.Own(available.Observe(vm.UnlinkedAccounts, Dispatcher, () => availableCount.Text = vm.UnlinkedAccounts.Count.ToString()));

        var top = Column(Translator.LinkedAccountsTitle, linkedCount, policy, linked);
        var bottom = Column(Translator.MergedAccountsAvailableAccountsTitle, availableCount, available);
        var columns = WinoLayout.VStack(WinoStyle.Space6, top, bottom);
        columns.Alignment = UIStackViewAlignment.Fill;
        return columns;
    }

    private static UIStackView Column(string title, UILabel count, params UIView[] views)
    {
        var heading = WinoStyle.Label(title, WinoStyle.CaptionStrong, WinoStyle.PrimaryText);
        heading.AccessibilityTraits = UIAccessibilityTrait.Header;
        count.Font = WinoStyle.Caption;
        var header = WinoLayout.HStack(WinoStyle.Space2, heading, count, WinoLayout.Spacer());
        header.Alignment = UIStackViewAlignment.FirstBaseline;
        var column = WinoLayout.VStack(6, header);
        column.Alignment = UIStackViewAlignment.Fill;
        foreach (var view in views) column.AddArrangedSubview(view);
        return column;
    }

    /// <summary>A raised account row: account picture or provider glyph, name and address, then Unlink or Link.</summary>
    private UIView AccountRow(AccountProviderDetailViewModel account, bool linked)
    {
        var surface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var picture = new WinoAccountIconView(28);
        if (account.Account is { } mailAccount) picture.Account = MailAccountIconInfoFactory.Create(mailAccount, pictures);

        var accountName = account.Account?.Name ?? string.Empty;
        var accountAddress = account.Account?.Address;
        var name = WinoStyle.Label(accountName, WinoStyle.Body, WinoStyle.PrimaryText);
        var address = WinoStyle.Label(accountAddress, WinoStyle.Caption, WinoStyle.SecondaryText);
        foreach (var label in new[] { name, address })
        {
            label.LineBreakMode = UILineBreakMode.TailTruncation;
            WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        }
        var text = WinoLayout.VStack(1, name, address);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        var command = linked ? ViewModel.UnlinkAccountCommand : ViewModel.LinkAccountCommand;
        var title = linked ? Translator.MergedAccount_Unlink : Translator.Link;
        var action = linked ? new SettingsPillButton(title) : new SettingsPillButton(title, WinoIconGlyph.Add);
        action.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        action.PrimaryActionTriggered += (_, _) => Execute(command, account);
        WinoAccessibility.Label(action, $"{title} {accountName}");

        var content = WinoLayout.HStack(10, picture, text, action);
        WinoLayout.Fill(content, surface, 0, 12, 0, 8);
        surface.AccessibilityLabel = string.IsNullOrEmpty(accountAddress) ? accountName : $"{accountName}, {accountAddress}";
        return surface;
    }

    private void Execute(ICommand command, object parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
