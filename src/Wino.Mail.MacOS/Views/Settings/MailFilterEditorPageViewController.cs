using AppKit;
using CoreAnimation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Mail filter editor (Manage accounts › account › Mail filters › Create mail filter, Windows
/// MailFilterEditorPage): the name, the "Runs on" choice between Wino and the provider, the watched
/// folder, the WHEN block (match mode and condition rows), the THEN block (action rows), the live
/// summary, Filter enabled and Stop processing, then Cancel and Save filter. Navigation parameter:
/// <see cref="MailFilterEditorNavigationParameter"/>. Rows are in MailFilterEditorPageViewController.Rows.cs.
/// </summary>
public sealed partial class MailFilterEditorPageViewController(MailFilterEditorPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailFilterEditorPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private string? _title;

    public string? PageTitle => _title;
    public string? PageDescription => ViewModel.Account is { } account
        ? string.Join(" · ", new[] { account.Name, account.Address }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct())
        : null;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var name = Bind.TextField(vm, nameof(vm.FilterName), s => s.FilterName, (s, v) => s.FilterName = v, Translator.MailFilterEditor_NamePlaceholder, 280);
        WinoAccessibility.Label(name, Translator.MailFilterEditor_Name);
        Add(Card(Translator.MailFilterEditor_Name, null, WinoIconGlyph.Tag, name));

        Add(SectionLabel(Translator.MailFilterEditor_Management));
        Add(ManagementOptions());

        var source = FolderPopUp(Bind, vm, s => s.Folders, vm.Folders, nameof(vm.SelectedSourceFolder), s => s.SelectedSourceFolder, (s, v) => s.SelectedSourceFolder = v, 240);
        WinoAccessibility.Label(source, Translator.MailFilterEditor_SourceFolder);
        var sourceCard = Card(Translator.MailFilterEditor_SourceFolder, null, WinoIconGlyph.Folder, source);
        Bind.Visible(Add(sourceCard), vm, nameof(vm.IsWinoManaged), s => s.IsWinoManaged);

        Add(Spacing(6));
        Add(WhenBlock());
        Add(Spacing(4));
        Add(ThenBlock());
        Add(Spacing(4));
        Add(SummaryStrip());
        Add(Spacing(4));

        Add(Card(Translator.MailFilterEditor_Enabled, Translator.MailFilterEditor_EnabledDescription, WinoIconGlyph.Sparkle,
            Bind.Switch(vm, nameof(vm.IsEnabled), s => s.IsEnabled, (s, v) => s.IsEnabled = v, Translator.MailFilterEditor_Enabled)));
        Add(Card(Translator.MailFilterEditor_StopProcessing, Translator.MailFilterEditor_StopProcessingDescription, WinoIconGlyph.Stop,
            Bind.Switch(vm, nameof(vm.StopProcessing), s => s.StopProcessing, (s, v) => s.StopProcessing = v, Translator.MailFilterEditor_StopProcessing)));

        var cancel = Bind.Button(Translator.Buttons_Cancel, () => vm.NavigationService.GoBack());
        cancel.KeyEquivalent = "\u001b";
        var save = Bind.Button(Translator.MailFilterEditor_Save, () => Run(vm.SaveAsync()), primary: true, icon: WinoIconGlyph.Save);
        Bind.Enabled(save, vm, nameof(vm.IsSaving), s => !s.IsSaving);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, save);
        buttons.EdgeInsets = new NSEdgeInsets(12, 0, 0, 0);
        Add(buttons);

        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        await ViewModel.LoadAsync(parameter!);
        // Same titles as the Windows breadcrumb: Create, Duplicate, or the filter's name when editing.
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _title = parameter switch
            {
                MailFilterEditorNavigationParameter { FilterId: not null } when !string.IsNullOrWhiteSpace(ViewModel.FilterName) => ViewModel.FilterName,
                MailFilterEditorNavigationParameter { DuplicateFilterId: not null } => Translator.MailFilterEditor_DuplicateTitle,
                _ => Translator.MailFilterEditor_CreateTitle
            };
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static NSView SectionLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, host, 10, 1, 2, 0);
        return host;
    }

    private static NSView Spacing(double height)
        => WinoLayout.Size(new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, -1, height);

    /// <summary>"Runs on": Wino on this device and Email provider as two pressable cards, the selected one outlined in accent.</summary>
    private NSView ManagementOptions()
    {
        var vm = ViewModel;
        var wino = new MailFilterManagementOptionView(WinoIconGlyph.Devices, Translator.MailFilterEditor_WinoManaged, Translator.MailFilterEditor_WinoManagedDescription);
        var provider = new MailFilterManagementOptionView(WinoIconGlyph.Cloud, Translator.MailFilterEditor_ProviderManaged, Translator.MailFilterEditor_ProviderManagedDescription);
        EventHandler selectWino = (_, _) => vm.SelectManagementType(MailFilterManagementType.WinoLocal);
        EventHandler selectProvider = (_, _) => vm.SelectManagementType(MailFilterManagementType.Provider);
        wino.Activated += selectWino;
        provider.Activated += selectProvider;
        Bindings.Own(new Infrastructure.ActionDisposable(() => { wino.Activated -= selectWino; provider.Activated -= selectProvider; }));

        Bind.Bind(vm, nameof(vm.SelectedManagementType), s => s.SelectedManagementType, _ =>
        {
            wino.IsSelected = vm.IsWinoManaged;
            provider.IsSelected = vm.IsProviderSelected;
        });
        Bind.Bind(vm, nameof(vm.CanChangeManagementType), s => s.CanChangeManagementType, enabled =>
        {
            wino.IsOptionEnabled = enabled;
            provider.IsOptionEnabled = enabled;
        });
        Bind.Visible(provider, vm, nameof(vm.IsProviderAvailable), s => s.IsProviderAvailable);

        var row = WinoLayout.HStack(WinoStyle.Space2, wino, provider);
        row.Alignment = NSLayoutAttribute.Top;
        row.Distribution = NSStackViewDistribution.FillEqually;
        return row;
    }

    /// <summary>
    /// A bordered block with a tinted header band (badge, title, optional trailing control) over a
    /// raised body, the Windows WHEN / THEN containers.
    /// </summary>
    private static (NSView Block, NSStackView Body) RuleBlock(string badge, string title, NSColor tone, NSView? trailing)
    {
        var badgeLabel = WinoStyle.Label(badge, NSFont.SystemFontOfSize(11, NSFontWeight.Bold), NSColor.White);
        var badgeView = new WinoSurfaceView { Fill = tone, CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(badgeLabel, badgeView, 3, 10, 3, 10);
        badgeView.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        badgeView.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        titleLabel.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        titleLabel.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var headerRow = WinoLayout.HStack(WinoStyle.Space3, badgeView, titleLabel);
        headerRow.Alignment = NSLayoutAttribute.CenterY;
        if (trailing is not null) headerRow.AddArrangedSubview(trailing);

        var band = new WinoSurfaceView
        {
            Fill = tone.ColorWithAlphaComponent(0.09f),
            CornerRadius = 7,
            Corners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(headerRow, band, 10, 16, 10, 16);
        band.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;

        var body = WinoLayout.VStack(WinoStyle.Space1);
        body.Alignment = NSLayoutAttribute.Leading;
        var bodySurface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            CornerRadius = 7,
            Corners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(body, bodySurface, 14, 16, 16, 16);

        var block = new WinoSurfaceView
        {
            Stroke = WinoStyle.ZoneStroke,
            StrokeWidth = 1,
            CornerRadius = WinoStyle.GroupRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var stack = WinoLayout.VStack(0, band, bodySurface);
        stack.Alignment = NSLayoutAttribute.Leading;
        band.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        bodySurface.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, block, 1);
        block.AccessibilityElement = true;
        block.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        block.AccessibilityLabel = $"{badge} {title}";
        return (block, body);
    }

    /// <summary>Adds a full-width row to a block body.</summary>
    private static T AddRow<T>(NSStackView body, T view) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        body.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        return view;
    }

    private NSView WhenBlock()
    {
        var vm = ViewModel;
        var match = Bind.Segmented(vm, [Translator.MailFilterEditor_MatchAll, Translator.MailFilterEditor_MatchAny],
            nameof(vm.SelectedMatchModeIndex), s => s.SelectedMatchModeIndex, (s, v) => s.SelectedMatchModeIndex = v);
        WinoAccessibility.Label(match, Translator.MailFilterEditor_Match);
        var (block, body) = RuleBlock(Translator.MailFilterEditor_WhenBadge, Translator.MailFilterEditor_WhenTitle, WinoStyle.Accent, match);

        AddRow(body, _conditionRows);
        var add = Bind.Button(Translator.MailFilterEditor_AddCondition, vm.AddCondition, icon: WinoIconGlyph.Add);
        var addRow = WinoLayout.HStack(0, add);
        addRow.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        body.AddArrangedSubview(addRow);

        Bind.Collection(vm.Conditions, QueueConditionsRebuild);
        return block;
    }

    private NSView ThenBlock()
    {
        var vm = ViewModel;
        var (block, body) = RuleBlock(Translator.MailFilterEditor_ThenBadge, Translator.MailFilterEditor_ThenTitle, WinoStyle.Success, null);

        var exclusive = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilterEditor_ExclusiveActionTitle, Translator.MailFilterEditor_ExclusiveActionDescription);
        var exclusiveHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(exclusive, exclusiveHost, 0, 0, 8, 0);
        Bind.Visible(AddRow(body, exclusiveHost), vm, nameof(vm.IsWinoManaged), s => s.IsWinoManaged);

        AddRow(body, _actionRows);

        var add = Bind.Button(Translator.MailFilterEditor_AddAction, vm.AddAction, icon: WinoIconGlyph.Add);
        var hint = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        hint.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        void UpdateAddAction()
        {
            add.Enabled = vm.CanAddAction;
            var exclusiveAction = vm.Actions.FirstOrDefault(action => IsExclusive(action.SelectedAction?.Value))?.SelectedAction;
            hint.StringValue = exclusiveAction is null ? string.Empty : string.Format(Translator.MailFilterEditor_ExclusiveActionHint, exclusiveAction.DisplayName);
            hint.Hidden = vm.CanAddAction || exclusiveAction is null;
        }
        Bind.Bind(vm, nameof(vm.CanAddAction), s => s.CanAddAction, _ => UpdateAddAction());
        var addRow = WinoLayout.HStack(WinoStyle.Space3, add, hint);
        addRow.Alignment = NSLayoutAttribute.CenterY;
        addRow.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        AddRow(body, addRow);

        Bind.Collection(vm.Actions, QueueActionsRebuild);
        return block;
    }

    /// <summary>The live natural-language summary under the blocks (Windows SubtleFillColorSecondary strip).</summary>
    private NSView SummaryStrip()
    {
        var vm = ViewModel;
        var icon = new WinoIconView(WinoIconGlyph.Info, 14, WinoStyle.Accent) { Colorful = false };
        WinoLayout.Size(icon, 14, 14);
        var text = Bind.Label(vm, nameof(vm.Summary), s => s.Summary, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(10, icon, text);
        row.Alignment = NSLayoutAttribute.Top;
        var strip = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = WinoSettingsStyle.CardRadius, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, strip, 12, 14, 12, 14);
        strip.AccessibilityElement = false;
        Bind.Visible(strip, vm, nameof(vm.Summary), s => !string.IsNullOrWhiteSpace(s.Summary));
        return strip;
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
