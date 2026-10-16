using CoreAnimation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Mail filter editor (Mac MailFilterEditorPageViewController, Windows MailFilterEditorPage): the name, the "Runs on"
/// choice between Wino and the provider (stacked at phone width), the watched folder, the WHEN block (match mode and
/// condition rows), the THEN block (action rows), the live summary, Filter enabled and Stop processing, then Cancel and
/// Save filter. Navigation parameter: <see cref="MailFilterEditorNavigationParameter"/>. Rows are in
/// MailFilterEditorPageViewController.Rows.cs.
/// </summary>
public sealed partial class MailFilterEditorPageViewController(MailFilterEditorPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailFilterEditorPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private string? _title;

    public string? PageTitle => _title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var name = Bind.TextField(vm, nameof(vm.FilterName), s => s.FilterName, (s, v) => s.FilterName = v, Translator.MailFilterEditor_NamePlaceholder, 0);
        name.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(name, Translator.MailFilterEditor_Name);
        Add(StackedCard(Translator.MailFilterEditor_Name, null, WinoIconGlyph.Tag, name));

        Add(SectionLabel(Translator.MailFilterEditor_Management));
        Add(ManagementOptions());

        var source = FolderPopUp(Bind, vm, s => s.Folders, vm.Folders, nameof(vm.SelectedSourceFolder), s => s.SelectedSourceFolder, (s, v) => s.SelectedSourceFolder = v);
        WinoAccessibility.Label(source, Translator.MailFilterEditor_SourceFolder);
        var sourceCard = StackedCard(Translator.MailFilterEditor_SourceFolder, null, WinoIconGlyph.Folder, source);
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
        var save = Bind.Button(Translator.MailFilterEditor_Save, () => Run(vm.SaveAsync()), primary: true, icon: WinoIconGlyph.Save);
        Bind.Enabled(save, vm, nameof(vm.IsSaving), s => !s.IsSaving);
        foreach (var button in new[] { cancel, save }) button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, save);
        WinoLayout.Insets(buttons, 12, 0, 0, 0);
        Add(buttons);

        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        await ViewModel.LoadAsync(parameter);
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

    private static UIView SectionLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText, 0);
        label.AccessibilityTraits = UIAccessibilityTrait.Header;
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, host, 10, 1, 2, 0);
        return host;
    }

    private static UIView Spacing(double height)
        => WinoLayout.Size(new UIView { TranslatesAutoresizingMaskIntoConstraints = false }, -1, height);

    /// <summary>"Runs on": Wino on this device and Email provider as two pressable cards, the selected one outlined in accent.</summary>
    private UIView ManagementOptions()
    {
        var vm = ViewModel;
        var wino = new MailFilterManagementOptionView(WinoIconGlyph.Devices, Translator.MailFilterEditor_WinoManaged, Translator.MailFilterEditor_WinoManagedDescription);
        var provider = new MailFilterManagementOptionView(WinoIconGlyph.Cloud, Translator.MailFilterEditor_ProviderManaged, Translator.MailFilterEditor_ProviderManagedDescription);
        EventHandler selectWino = (_, _) => vm.SelectManagementType(MailFilterManagementType.WinoLocal);
        EventHandler selectProvider = (_, _) => vm.SelectManagementType(MailFilterManagementType.Provider);
        wino.Clicked += selectWino;
        provider.Clicked += selectProvider;
        Bindings.Own(new ActionDisposable(() => { wino.Clicked -= selectWino; provider.Clicked -= selectProvider; }));

        Bind.Bind(vm, nameof(vm.SelectedManagementType), s => s.SelectedManagementType, _ =>
        {
            wino.IsSelected = vm.IsWinoManaged;
            provider.IsSelected = vm.IsProviderSelected;
        });
        Bind.Bind(vm, nameof(vm.CanChangeManagementType), s => s.CanChangeManagementType, enabled =>
        {
            wino.Enabled = enabled;
            provider.Enabled = enabled;
        });
        Bind.Visible(provider, vm, nameof(vm.IsProviderAvailable), s => s.IsProviderAvailable);

        var stack = WinoLayout.VStack(WinoStyle.Space2, wino, provider);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>
    /// A bordered block with a tinted header band (badge, title, optional control under them) over a raised body, the
    /// Windows WHEN / THEN containers.
    /// </summary>
    private static (UIView Block, UIStackView Body) RuleBlock(string badge, string title, UIColor tone, UIView? trailing)
    {
        var badgeLabel = WinoStyle.Label(badge, WinoStyle.Scaled(11, UIFontWeight.Bold), UIColor.White);
        var badgeView = new WinoSurfaceView { Fill = tone, CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(badgeLabel, badgeView, 3, 10, 3, 10);
        WinoLayout.Priorities(badgeView, UILayoutConstraintAxis.Horizontal, 750, 750);
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        titleLabel.AccessibilityTraits = UIAccessibilityTrait.Header;
        WinoLayout.Priorities(titleLabel, UILayoutConstraintAxis.Horizontal, 1, 250);
        var headerRow = WinoLayout.HStack(WinoStyle.Space3, badgeView, titleLabel);
        var header = WinoLayout.VStack(WinoStyle.Space2, headerRow);
        header.Alignment = UIStackViewAlignment.Fill;
        // The match-mode control sits under the badge at phone width (trailing on the Mac).
        if (trailing is not null) header.AddArrangedSubview(trailing);

        var band = new WinoSurfaceView
        {
            Fill = tone.ColorWithAlpha(0.09f),
            CornerRadius = 7,
            Corners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(header, band, 10, 14, 10, 14);
        band.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;

        var body = WinoLayout.VStack(WinoStyle.Space1);
        body.Alignment = UIStackViewAlignment.Fill;
        var bodySurface = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            CornerRadius = 7,
            Corners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(body, bodySurface, 12, 12, 14, 12);

        var block = new WinoSurfaceView
        {
            Stroke = WinoStyle.ZoneStroke,
            StrokeWidth = 1,
            CornerRadius = WinoStyle.GroupRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var stack = WinoLayout.VStack(0, band, bodySurface);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, block, 1);
        block.AccessibilityLabel = $"{badge} {title}";
        return (block, body);
    }

    private UIView WhenBlock()
    {
        var vm = ViewModel;
        var match = Bind.Segmented(vm, [Translator.MailFilterEditor_MatchAll, Translator.MailFilterEditor_MatchAny],
            nameof(vm.SelectedMatchModeIndex), s => s.SelectedMatchModeIndex, (s, v) => s.SelectedMatchModeIndex = v);
        WinoAccessibility.Label(match, Translator.MailFilterEditor_Match);
        var (block, body) = RuleBlock(Translator.MailFilterEditor_WhenBadge, Translator.MailFilterEditor_WhenTitle, WinoStyle.Accent, match);

        body.AddArrangedSubview(_conditionRows);
        var add = Bind.Button(Translator.MailFilterEditor_AddCondition, vm.AddCondition, icon: WinoIconGlyph.Add);
        add.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var addRow = WinoLayout.HStack(0, add, WinoLayout.Spacer());
        WinoLayout.Insets(addRow, 8, 0, 0, 0);
        body.AddArrangedSubview(addRow);

        Bind.Collection(vm.Conditions, QueueConditionsRebuild);
        return block;
    }

    private UIView ThenBlock()
    {
        var vm = ViewModel;
        var (block, body) = RuleBlock(Translator.MailFilterEditor_ThenBadge, Translator.MailFilterEditor_ThenTitle, WinoStyle.Success, null);

        var exclusive = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilterEditor_ExclusiveActionTitle, Translator.MailFilterEditor_ExclusiveActionDescription);
        var exclusiveHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(exclusive, exclusiveHost, 0, 0, 8, 0);
        body.AddArrangedSubview(exclusiveHost);
        Bind.Visible(exclusiveHost, vm, nameof(vm.IsWinoManaged), s => s.IsWinoManaged);

        body.AddArrangedSubview(_actionRows);

        var add = Bind.Button(Translator.MailFilterEditor_AddAction, vm.AddAction, icon: WinoIconGlyph.Add);
        add.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var hint = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        void UpdateAddAction()
        {
            add.Enabled = vm.CanAddAction;
            var exclusiveAction = vm.Actions.FirstOrDefault(action => MailFilterDisplay.IsExclusiveAction(action.SelectedAction?.Value))?.SelectedAction;
            hint.Text = exclusiveAction is null ? string.Empty : string.Format(Translator.MailFilterEditor_ExclusiveActionHint, exclusiveAction.DisplayName);
            hint.Hidden = vm.CanAddAction || exclusiveAction is null;
        }
        Bind.Bind(vm, nameof(vm.CanAddAction), s => s.CanAddAction, _ => UpdateAddAction());
        var addRow = WinoLayout.VStack(WinoStyle.Space2, add, hint);
        addRow.Alignment = UIStackViewAlignment.Leading;
        WinoLayout.Insets(addRow, 8, 0, 0, 0);
        body.AddArrangedSubview(addRow);

        Bind.Collection(vm.Actions, QueueActionsRebuild);
        return block;
    }

    /// <summary>The live natural-language summary under the blocks (Windows SubtleFillColorSecondary strip).</summary>
    private UIView SummaryStrip()
    {
        var vm = ViewModel;
        var icon = new WinoIconView(WinoIconGlyph.Info, 14, WinoStyle.Accent) { Colorful = false };
        WinoLayout.Size(icon, 14, 14);
        var text = Bind.Label(vm, nameof(vm.Summary), s => s.Summary, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var row = WinoLayout.HStack(10, icon, text);
        row.Alignment = UIStackViewAlignment.Top;
        var strip = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = WinoSettingsStyle.CardRadius, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, strip, 12, 14, 12, 14);
        Bind.Visible(strip, vm, nameof(vm.Summary), s => !string.IsNullOrWhiteSpace(s.Summary));
        return strip;
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
