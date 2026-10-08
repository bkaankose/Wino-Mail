using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Linked inbox details (Manage accounts › linked inbox, Windows MergedAccountDetailsPage): the name
/// edited in place, the mail-only notice, the Manage link expander with Linked and Available accounts
/// side by side (Link / Unlink buttons, or drag a row to the other column), the two-account rule, Save
/// changes, and Unlink accounts under Remove. The window title follows the linked inbox name.
/// </summary>
public sealed class MergedAccountDetailsPageViewController(MergedAccountDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MergedAccountDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const string LinkedKey = "linked";
    private const string AvailableKey = "available";
    private const double RowHeight = 48;
    private const double RowSpacing = 6;

    private readonly SettingsDragGroup _group = new();

    public string? PageTitle => ViewModel.EditingMergedAccount?.MergedInbox?.Name is { Length: > 0 } name ? name : ViewModel.MergedAccountName;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Bind.Bind(vm, nameof(vm.EditingMergedAccount), s => s.EditingMergedAccount, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        Add(Card(Translator.SettingsRenameMergeAccount_Title, Translator.SettingsRenameMergeAccount_Description, WinoIconGlyph.Rename, NameField()));

        var notice = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.MergedAccount_MailOnlyMessage);
        Add(notice);

        var manage = Expander(Translator.SettingsManageLink_Title, Translator.SettingsManageLink_Description, WinoIconGlyph.PeopleLink, null);
        manage.Add(Columns(), 12, WinoSettingsStyle.CardPadding, 14, WinoSettingsStyle.CardPadding);
        manage.IsExpanded = true;
        Add(manage);

        var save = Bind.Button(Translator.SettingsLinkedAccountsSave_Title, vm.SaveChangesCommand, primary: true);
        Add(Card(Translator.SettingsLinkedAccountsSave_Title, Translator.SettingsLinkedAccountsSave_Description, WinoIconGlyph.Save, save));

        var unlink = Bind.Button(Translator.MergedAccount_Unlink + "…", vm.UnlinkAccountsCommand);
        unlink.ContentTintColor = WinoStyle.Critical;
        AddGroup(Translator.Buttons_Remove,
            Card(Translator.SettingsUnlinkAccounts_Title, Translator.SettingsUnlinkAccounts_Description, WinoIconGlyph.SubtractCircle, unlink));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    /// <summary>The inline name field: shows the current name and renames on Return or when focus leaves.</summary>
    private NSTextField NameField()
    {
        var vm = ViewModel;
        var field = new NSTextField
        {
            Font = WinoStyle.Body,
            PlaceholderString = Translator.SettingsRenameMergeAccount_Title,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.Cell.SetSendsActionOnEndEditing(true);
        field.WidthAnchor.ConstraintEqualTo(220).Active = true;
        WinoAccessibility.Label(field, Translator.SettingsRenameMergeAccount_Title);
        Bind.Bind(vm, nameof(vm.MergedAccountName), s => s.MergedAccountName, name =>
        {
            if (field.CurrentEditor is null && field.StringValue != (name ?? string.Empty)) field.StringValue = name ?? string.Empty;
        });
        Bind.OnActivated(field, () =>
        {
            var name = field.StringValue?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                field.StringValue = vm.MergedAccountName ?? string.Empty;
                return;
            }
            Run(vm.ApplyMergedAccountNameAsync(name));
        });
        return field;
    }

    /// <summary>Linked accounts on the left, Available accounts on the right, each with its header.</summary>
    private NSView Columns()
    {
        var vm = ViewModel;
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
        Bindings.Own(linked.Observe(vm.LinkedAccounts, Dispatcher, () => linkedCount.StringValue = vm.LinkedAccounts.Count.ToString()));

        var availableCount = Caption(string.Empty);
        var available = new SettingsDragList<AccountProviderDetailViewModel>(AvailableKey, vm.UnlinkedAccounts, _group, (account, _) => AccountRow(account, linked: false), RowHeight, RowSpacing);
        available.SetFooterView(new DashedDropZoneView(Translator.MergedAccount_AvailableDropHint), RowHeight);
        Bindings.Own(available.Observe(vm.UnlinkedAccounts, Dispatcher, () => availableCount.StringValue = vm.UnlinkedAccounts.Count.ToString()));

        var left = Column(Translator.LinkedAccountsTitle, linkedCount, policy, linked);
        var right = Column(Translator.MergedAccountsAvailableAccountsTitle, availableCount, available);
        var columns = WinoLayout.HStack(WinoStyle.Space4, left, right);
        columns.Alignment = NSLayoutAttribute.Top;
        columns.Distribution = NSStackViewDistribution.FillEqually;
        return columns;
    }

    private static NSStackView Column(string title, NSTextField count, params NSView[] views)
    {
        var heading = WinoStyle.Label(title, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        count.Font = WinoStyle.Caption;
        var header = WinoLayout.HStack(WinoStyle.Space2, heading, count);
        header.Alignment = NSLayoutAttribute.FirstBaseline;
        var column = WinoLayout.VStack(6, header);
        column.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in views)
        {
            column.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        return column;
    }

    /// <summary>
    /// A 48pt raised account row: drag handle (linked rows; available rows keep its space), account
    /// picture or provider glyph, name and address, then Unlink or Link.
    /// </summary>
    private NSView AccountRow(AccountProviderDetailViewModel account, bool linked)
    {
        var surface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        NSView handle = linked
            ? WinoLayout.Size(new WinoIconView(WinoIconGlyph.ReOrderDotsVertical, 14, WinoStyle.TertiaryText), 14, 14)
            : SettingsRowParts.Slot(14, 14);
        var picture = new WinoAccountIconView(24);
        if (account.Account is { } mailAccount) picture.Account = MailAccountIconInfoFactory.Create(mailAccount, pictures);

        var name = WinoStyle.Label(account.Account?.Name, WinoStyle.Body, WinoStyle.PrimaryText);
        var address = WinoStyle.Label(account.Account?.Address, WinoStyle.Caption, WinoStyle.SecondaryText);
        foreach (var label in new[] { name, address })
        {
            label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        }
        var text = WinoLayout.VStack(1, name, address);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var accountName = account.Account?.Name ?? string.Empty;
        SettingsPillButton action;
        if (linked)
        {
            action = new SettingsPillButton(Translator.MergedAccount_Unlink);
            action.Activated += (_, _) => Execute(ViewModel.UnlinkAccountCommand, account);
            WinoAccessibility.Label(action, $"{Translator.MergedAccount_Unlink} {accountName}");
        }
        else
        {
            action = new SettingsPillButton(Translator.Link, WinoIconGlyph.Add);
            action.Activated += (_, _) => Execute(ViewModel.LinkAccountCommand, account);
            WinoAccessibility.Label(action, $"{Translator.Link} {accountName}");
        }

        var content = WinoLayout.HStack(10, handle, picture, text, action);
        content.Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Fill(content, surface, 0, 10, 0, 10);
        WinoAccessibility.Label(surface, string.IsNullOrEmpty(account.Account?.Address) ? accountName : $"{accountName}, {account.Account!.Address}");
        return surface;
    }

    private void Execute(System.Windows.Input.ICommand command, object parameter)
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
