using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Mail filter editor (iOS MailFilterEditorPageViewController, Windows MailFilterEditorPage): the name, the "Runs on"
/// choice between Wino and the provider (stacked at phone width), the watched folder, the WHEN block (match mode and
/// condition rows), the THEN block (action rows), the live summary, Filter enabled and Stop processing, then Cancel and
/// Save filter. Navigation parameter: <see cref="MailFilterEditorNavigationParameter"/>. Rows are in
/// MailFilterEditorPageFragment.Rows.cs.
/// </summary>
public sealed partial class MailFilterEditorPageFragment : SettingsPageFragment<MailFilterEditorPageViewModel>, ISettingsPageTitleSource
{
    private const string TitleKey = "MailFilterEditor.Title";

    /// <summary>Same titles as the Windows breadcrumb: Create, Duplicate, or the filter's name when editing.</summary>
    public string? PageTitle => Entry?.Parameter switch
    {
        MailFilterEditorNavigationParameter { FilterId: not null } => Entry.State.TryGetValue(TitleKey, out var title) ? title as string : null,
        MailFilterEditorNavigationParameter { DuplicateFilterId: not null } => Translator.MailFilterEditor_DuplicateTitle,
        _ => Translator.MailFilterEditor_CreateTitle
    };

    // The account line replaces the catalogue description (iOS PageDescription).
    protected override bool ShowsPageDescription => false;

    /// <summary>The route initializer (iOS InitializeAsync): Windows navigation, then the filter.</summary>
    public static async Task InitializeAsync(MailFilterEditorPageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        viewModel.OnNavigatedTo(mode, parameter);
        if (parameter is not null) await viewModel.LoadAsync(parameter);
    }

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() =>
        {
            _conditionScope?.Dispose();
            _actionScope?.Dispose();
        }));

        var intro = AddIntro(SettingsCatalog.Description(SettingsPage) ?? string.Empty);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
        {
            var line = account is null ? null
                : string.Join(" · ", new[] { account.Name, account.Address }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
            intro.Text = string.IsNullOrWhiteSpace(line) ? SettingsCatalog.Description(SettingsPage) ?? string.Empty : line;
            intro.Visibility = string.IsNullOrWhiteSpace(intro.Text) ? ViewStates.Gone : ViewStates.Visible;
        });
        // The title names the filter being edited as it was loaded.
        Bind.Bind(vm, nameof(vm.FilterName), s => s.FilterName, name =>
        {
            if (Entry is { } entry && entry.Parameter is MailFilterEditorNavigationParameter { FilterId: not null }
                && !entry.State.ContainsKey(TitleKey) && !string.IsNullOrWhiteSpace(name))
            {
                entry.State[TitleKey] = name;
                UpdateTitle();
            }
        });

        var name = Bind.TextField(vm, nameof(vm.FilterName), s => s.FilterName, (s, v) => s.FilterName = v, Translator.MailFilterEditor_NamePlaceholder);
        name.ContentDescription = Translator.MailFilterEditor_Name;
        Add(StackedCard(Translator.MailFilterEditor_Name, null, WinoIconGlyph.Tag, name));

        Add(SectionLabel(Translator.MailFilterEditor_Management));
        Add(ManagementOptions());

        var source = FolderPopUp(Bind, vm, s => s.Folders, nameof(vm.SelectedSourceFolder), s => s.SelectedSourceFolder, (s, v) => s.SelectedSourceFolder = v, vm.Folders);
        source.ContentDescription = Translator.MailFilterEditor_SourceFolder;
        var sourceCard = StackedCard(Translator.MailFilterEditor_SourceFolder, null, WinoIconGlyph.Folder, source);
        Bind.Visible(Add(sourceCard), vm, nameof(vm.IsWinoManaged), s => s.IsWinoManaged);

        Spaced(Add(WhenBlock()), Resource.Dimension.wino_space_sm);
        Spaced(Add(ThenBlock()), Resource.Dimension.wino_space_s);
        Spaced(Add(SummaryStrip()), Resource.Dimension.wino_space_s);

        Spaced(Add(Card(Translator.MailFilterEditor_Enabled, Translator.MailFilterEditor_EnabledDescription, WinoIconGlyph.Sparkle,
            Bind.Switch(vm, nameof(vm.IsEnabled), s => s.IsEnabled, (s, v) => s.IsEnabled = v, Translator.MailFilterEditor_Enabled))), Resource.Dimension.wino_space_s);
        Add(Card(Translator.MailFilterEditor_StopProcessing, Translator.MailFilterEditor_StopProcessingDescription, WinoIconGlyph.Stop,
            Bind.Switch(vm, nameof(vm.StopProcessing), s => s.StopProcessing, (s, v) => s.StopProcessing = v, Translator.MailFilterEditor_StopProcessing)));

        var cancel = Bind.Button(Translator.Buttons_Cancel, () => vm.NavigationService.GoBack());
        var save = Bind.Button(Translator.MailFilterEditor_Save, () => AccountSettingsParts.Run(vm.SaveAsync(), ReportError), primary: true, icon: WinoIconGlyph.Save);
        Bind.Enabled(save, vm, nameof(vm.IsSaving), s => !s.IsSaving);
        var buttons = AccountSettingsParts.HRow(Ctx);
        buttons.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);
        buttons.AddView(cancel);
        buttons.AddView(save, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_s));
        Spaced(Add(buttons), Resource.Dimension.wino_space_m);
    }

    private void Spaced(View view, int top)
    {
        if (view.LayoutParameters is LinearLayout.LayoutParams layout) layout.TopMargin = WinoTokens.Px(Ctx, top);
    }

    private TextView SectionLabel(string text)
    {
        var label = Text(text, WinoTextStyle.BodyLargeStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) label.AccessibilityHeading = true;
        WinoTokens.Pad(label, Resource.Dimension.wino_space_hairline, Resource.Dimension.wino_space_sm, 0, Resource.Dimension.wino_space_3xs);
        return label;
    }

    /// <summary>"Runs on": Wino on this device and Email provider as two pressable cards, the selected one outlined in accent.</summary>
    private View ManagementOptions()
    {
        var vm = VM;
        var wino = new ManagementOption(Ctx, WinoIconGlyph.Devices, Translator.MailFilterEditor_WinoManaged, Translator.MailFilterEditor_WinoManagedDescription);
        var provider = new ManagementOption(Ctx, WinoIconGlyph.Cloud, Translator.MailFilterEditor_ProviderManaged, Translator.MailFilterEditor_ProviderManagedDescription);
        Bind.Views.Click(wino.View, () => vm.SelectManagementType(MailFilterManagementType.WinoLocal));
        Bind.Views.Click(provider.View, () => vm.SelectManagementType(MailFilterManagementType.Provider));

        Bind.Bind(vm, nameof(vm.SelectedManagementType), s => s.SelectedManagementType, _ =>
        {
            wino.IsSelected = vm.IsWinoManaged;
            provider.IsSelected = vm.IsProviderSelected;
        });
        Bind.Bind(vm, nameof(vm.CanChangeManagementType), s => s.CanChangeManagementType, enabled =>
        {
            wino.IsEnabled = enabled;
            provider.IsEnabled = enabled;
        });
        Bind.Visible(provider.View, vm, nameof(vm.IsProviderAvailable), s => s.IsProviderAvailable);
        return Stack(Resource.Dimension.wino_space_s, wino.View, provider.View);
    }

    /// <summary>
    /// A bordered block with a tinted header band (badge, title, optional control under them) over a raised body, the
    /// Windows WHEN / THEN containers.
    /// </summary>
    private (View Block, LinearLayout Body) RuleBlock(string badge, string title, Color tone, View? trailing)
    {
        var badgeLabel = AccountSettingsParts.Label(Ctx, badge, WinoTextStyle.CaptionStrong, WinoTokens.Color(Ctx, Resource.Color.wino_on_badge), maxLines: 1);
        WinoTokens.Pad(badgeLabel, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_3xs);
        badgeLabel.Background = AccountSettingsParts.Surface(Ctx, tone, null, Resource.Dimension.wino_radius_small);
        var titleLabel = Text(title, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) titleLabel.AccessibilityHeading = true;
        var headerRow = AccountSettingsParts.HRow(Ctx);
        headerRow.AddView(badgeLabel);
        headerRow.AddView(titleLabel, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m));
        var header = Stack(Resource.Dimension.wino_space_s, headerRow);
        // The match-mode control sits under the badge at phone width (trailing on the Mac).
        if (trailing is not null) AddTo(header, trailing, Resource.Dimension.wino_space_s);
        WinoTokens.Pad(header, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_sm);
        header.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch));
        header.Background = Corners(WinoColors.WithAlpha(tone, 0.09), top: true);

        var body = AccountSettingsParts.VColumn(Ctx);
        WinoTokens.Pad(body, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_ml);
        body.Background = Corners(AccountSettingsParts.RowFill(Ctx), top: false);

        var block = AccountSettingsParts.VColumn(Ctx);
        var inset = Resource.Dimension.wino_space_hairline;
        WinoTokens.Pad(block, inset, inset, inset, inset);
        block.Background = AccountSettingsParts.Surface(Ctx, Color.Transparent, WinoColors.ZoneStroke(Ctx), Resource.Dimension.wino_radius_large);
        block.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        block.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        block.ContentDescription = $"{badge} {title}";
        return (block, body);
    }

    private GradientDrawable Corners(Color fill, bool top)
    {
        // The block's radius less its hairline inset, so the bands follow the outline.
        var radius = WinoTokens.PxF(Ctx, Resource.Dimension.wino_radius_large) - WinoTokens.PxF(Ctx, Resource.Dimension.wino_space_hairline);
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadii(top ? [radius, radius, radius, radius, 0, 0, 0, 0] : [0, 0, 0, 0, radius, radius, radius, radius]);
        return drawable;
    }

    private View WhenBlock()
    {
        var vm = VM;
        var match = Bind.Segmented(vm, [Translator.MailFilterEditor_MatchAll, Translator.MailFilterEditor_MatchAny],
            nameof(vm.SelectedMatchModeIndex), s => s.SelectedMatchModeIndex, (s, v) => s.SelectedMatchModeIndex = v);
        match.ContentDescription = Translator.MailFilterEditor_Match;
        var (block, body) = RuleBlock(Translator.MailFilterEditor_WhenBadge, Translator.MailFilterEditor_WhenTitle, WinoColors.Accent(Ctx), match);

        _conditionRows = AccountSettingsParts.VColumn(Ctx);
        AccountSettingsParts.AddStretched(body, _conditionRows);
        var add = Bind.Button(Translator.MailFilterEditor_AddCondition, vm.AddCondition, icon: WinoIconGlyph.Add);
        AccountSettingsParts.AddStretched(body, Row(add), Resource.Dimension.wino_space_s);

        Bind.Collection(vm.Conditions, QueueConditionsRebuild);
        return block;
    }

    private View ThenBlock()
    {
        var vm = VM;
        var (block, body) = RuleBlock(Translator.MailFilterEditor_ThenBadge, Translator.MailFilterEditor_ThenTitle, WinoColors.Success(Ctx), null);

        var exclusive = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilterEditor_ExclusiveActionTitle, Translator.MailFilterEditor_ExclusiveActionDescription);
        AccountSettingsParts.AddStretched(body, exclusive);
        ((LinearLayout.LayoutParams)exclusive.LayoutParameters!).BottomMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);
        Bind.Visible(exclusive, vm, nameof(vm.IsWinoManaged), s => s.IsWinoManaged);

        _actionRows = AccountSettingsParts.VColumn(Ctx);
        AccountSettingsParts.AddStretched(body, _actionRows);

        var add = Bind.Button(Translator.MailFilterEditor_AddAction, vm.AddAction, icon: WinoIconGlyph.Add);
        var hint = Caption(string.Empty);
        void UpdateAddAction()
        {
            SettingsBinder.SetEnabled(add, vm.CanAddAction);
            var exclusiveAction = vm.Actions.FirstOrDefault(action => MailFilterDisplay.IsExclusiveAction(action.SelectedAction?.Value))?.SelectedAction;
            hint.Text = exclusiveAction is null ? string.Empty : string.Format(Translator.MailFilterEditor_ExclusiveActionHint, exclusiveAction.DisplayName);
            hint.Visibility = vm.CanAddAction || exclusiveAction is null ? ViewStates.Gone : ViewStates.Visible;
        }
        Bind.Bind(vm, nameof(vm.CanAddAction), s => s.CanAddAction, _ => UpdateAddAction());
        AccountSettingsParts.AddStretched(body, Stack(Resource.Dimension.wino_space_s, Row(add), hint), Resource.Dimension.wino_space_s);

        Bind.Collection(vm.Actions, QueueActionsRebuild);
        return block;
    }

    /// <summary>The live natural-language summary under the blocks (Windows SubtleFillColorSecondary strip).</summary>
    private View SummaryStrip()
    {
        var vm = VM;
        var row = AccountSettingsParts.HRow(Ctx);
        row.SetGravity(GravityFlags.Top);
        row.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Info, Resource.Dimension.wino_glyph_s, WinoColors.AccentText(Ctx), colorful: false));
        var text = Bind.Label(vm, nameof(vm.Summary), s => s.Summary, WinoTextStyle.Description, maximumLines: 0);
        row.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_sm));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_m);
        row.Background = AccountSettingsParts.Surface(Ctx, WinoColors.SubtleFill(Ctx), null);
        Bind.Visible(row, vm, nameof(vm.Summary), s => !string.IsNullOrWhiteSpace(s.Summary));
        return row;
    }

    /// <summary>
    /// One "Runs on" choice (iOS MailFilterManagementOptionView): a 40 dp accent tile, the title and its explanation,
    /// and a checkmark when selected. The selected card has a 2 dp accent outline.
    /// </summary>
    private sealed class ManagementOption
    {
        private readonly global::Android.Content.Context _context;
        private readonly WinoIconView _check;
        private bool _isSelected;
        private bool _isEnabled = true;

        public ManagementOption(global::Android.Content.Context context, WinoIconGlyph glyph, string title, string description)
        {
            _context = context;
            var row = AccountSettingsParts.HRow(context);
            row.SetGravity(GravityFlags.Top);
            var (tile, _) = AccountSettingsParts.Tile(context, glyph, WinoColors.Accent(context), Resource.Dimension.wino_size_control, Resource.Dimension.wino_glyph_l);
            row.AddView(tile);
            var text = AccountSettingsParts.VColumn(context);
            text.AddView(AccountSettingsParts.Label(context, title, WinoTextStyle.BodyStrong));
            text.AddView(AccountSettingsParts.Label(context, description, WinoTextStyle.Caption));
            row.AddView(text, AccountSettingsParts.RowItem(context, weight: 1, marginStart: Resource.Dimension.wino_space_m));
            _check = AccountSettingsParts.Glyph(context, WinoIconGlyph.Checkmark, Resource.Dimension.wino_glyph_m, WinoColors.AccentText(context), colorful: false);
            row.AddView(_check, AccountSettingsParts.RowItem(context, marginStart: Resource.Dimension.wino_space_m));
            WinoTokens.Pad(row, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_ml, Resource.Dimension.wino_space_m);
            row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large));
            row.Clickable = true;
            row.Focusable = true;
            row.ContentDescription = $"{title}. {description}";
            View = row;
            Apply();
        }

        public LinearLayout View { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; Apply(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; Apply(); }
        }

        private void Apply()
        {
            View.Background = AccountSettingsParts.Surface(_context, WinoColors.CardFill(_context),
                _isSelected ? WinoColors.Accent(_context) : WinoColors.CardStroke(_context), Resource.Dimension.wino_radius_large,
                strokeWidth: _isSelected ? Resource.Dimension.wino_space_3xs : Resource.Dimension.wino_space_hairline);
            _check.Visibility = _isSelected ? ViewStates.Visible : ViewStates.Invisible;
            View.Enabled = _isEnabled;
            View.Alpha = _isEnabled ? 1f : 0.55f;
            View.Selected = _isSelected;
        }
    }
}
