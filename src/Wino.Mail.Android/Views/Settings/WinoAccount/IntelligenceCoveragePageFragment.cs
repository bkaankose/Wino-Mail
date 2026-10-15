using System.Globalization;
using Android.Content.Res;
using Orientation = Android.Widget.Orientation;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Slider;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// The coverage editor (Windows IntelligenceCoveragePage, iOS IntelligenceCoveragePageViewController): totals with
/// Cancel / Apply, the folder tree, and the selected folder's rule — mode, presets, histogram with a slider or range
/// selector, and the three band counts. The Mac puts the tree and the editor side by side; on phone width they stack,
/// the tree first (every folder expanded, as the Mac opens it).
/// </summary>
public sealed class IntelligenceCoveragePageFragment : SettingsPageFragment<IntelligenceCoveragePageViewModel>
{
    private const int SectionSpacing = Resource.Dimension.wino_space_m;
    private readonly List<CoverageFolderCell> _cells = [];
    private readonly List<(MaterialButton Button, Func<bool> IsActive)> _presetButtons = [];

    private void Watch<TValue>(Func<IntelligenceCoveragePageViewModel, TValue> read, Action<TValue> apply)
        => Bind.Views.BindAny(VM, read, apply);

    private static void Show(View view, bool visible) => view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;

    /// <summary>The route's initializer: Back with a fresh ViewModel must still load the editor arguments.</summary>
    internal static Task InitializeAsync(IntelligenceCoveragePageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        viewModel.OnNavigatedTo(NavigationMode.New, parameter);
        return Task.CompletedTask;
    }

    protected override void BuildPage()
    {
        var vm = VM;
        _cells.Clear();
        _presetButtons.Clear();

        var totals = Text(string.Empty);
        Watch(s => (s.TotalSummary, s.ScopeSummary), state =>
        {
            var builder = new SpannableStringBuilder();
            var total = state.TotalSummary ?? string.Empty;
            builder.Append(total);
            builder.SetSpan(new TextAppearanceSpan(Ctx, WinoTokens.StyleResource(WinoTextStyle.BodyStrong)), 0, total.Length, SpanTypes.ExclusiveExclusive);
            int start = builder.Length();
            builder.Append(" · " + state.ScopeSummary);
            builder.SetSpan(new TextAppearanceSpan(Ctx, WinoTokens.StyleResource(WinoTextStyle.BodySecondary)), start, builder.Length(), SpanTypes.ExclusiveExclusive);
            totals.TextFormatted = builder;
        });
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        var apply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCommand, primary: true);
        var gap = IntelligenceViews.Spacer(Ctx);
        var buttons = IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_s, gap, gap, cancel, apply);
        AddSection(Stack(Resource.Dimension.wino_space_sm, totals, buttons));

        var folderRows = Stack(0);
        AddSection(FolderPanel(folderRows));
        var histogram = new CoverageHistogramView(Ctx);
        AddSection(EditorPanel(histogram));

        Bind.Collection(vm.RootFolders, () => ReloadTree(folderRows));
        Watch(s => s.SelectedFolder, _ => RefreshRows());
        Bind.Collection(vm.Buckets, () => histogram.SetBuckets(vm.Buckets.ToList()));
    }

    private void AddSection(View view)
    {
        Add(view);
        if (Page.ChildCount > 1 && view.LayoutParameters is LinearLayout.LayoutParams layout) layout.TopMargin = WinoTokens.Px(Ctx, SectionSpacing);
    }

    private View FolderPanel(LinearLayout rows)
    {
        var title = Text(Translator.SemanticIndex_CoverageIncludedFolders, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        WinoTokens.Pad(title, Resource.Dimension.wino_space_l, 0, Resource.Dimension.wino_space_l, 0);
        var stack = Stack(Resource.Dimension.wino_space_s, title, rows);
        WinoTokens.Pad(stack, 0, Resource.Dimension.wino_space_m, 0, Resource.Dimension.wino_space_s);
        // Totals change whenever a rule does, and Apply to all can change every folder's included dot and accessibility
        // name, so every row is reconfigured in place.
        Watch(s => s.TotalSummary, _ => RefreshRows());
        return IntelligenceViews.Surface(Ctx, stack, 0);
    }

    private View EditorPanel(CoverageHistogramView histogram)
    {
        var vm = VM;
        var context = Ctx;
        var name = Text(string.Empty, WinoTextStyle.Title);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) name.AccessibilityHeading = true;
        Watch(s => s.SelectedFolderName, text => name.Text = text);
        var applyAll = Bind.Button(Translator.SemanticIndex_CoverageApplyToAll, vm.ApplyToAllFoldersCommand);
        var clear = Bind.Button(Translator.SemanticIndex_CoverageClearFolder, vm.ClearFolderCommand);
        TooltipCompat.SetTooltipText(clear, Translator.SemanticIndex_CoverageClearFolderTooltip);
        Watch(s => s.IsEditorVisible, on =>
        {
            applyAll.Enabled = on;
            clear.Enabled = on;
        });
        var top = Stack(Resource.Dimension.wino_space_sm, name, IntelligenceViews.Flow(context, [applyAll, clear], Resource.Dimension.wino_space_s));

        var notice = Text(Translator.SemanticIndex_CoverageFolderEmptyNotice, WinoTextStyle.BodySecondary);
        Watch(s => s.IsEmptyFolderNoticeVisible, on => Show(notice, on));

        // 1. Mode (the Mac radio pair as a segmented control).
        var modes = Bind.Segmented(vm, [Translator.SemanticIndex_CoverageModeLatestCount, Translator.SemanticIndex_CoverageModeDateRange],
            nameof(vm.CoverageModeIndex), s => s.CoverageModeIndex, (s, v) => s.CoverageModeIndex = v);
        var mode = Stack(Resource.Dimension.wino_space_sm, SectionTitle(Translator.SemanticIndex_CoverageModeTitle), modes);

        // 2. Presets for the active mode.
        var presetTitle = SectionTitle(string.Empty);
        var presetFlow = IntelligenceViews.Flow(context, [], Resource.Dimension.wino_space_s);
        Watch(s => s.IsLatestCountMode, count =>
        {
            presetTitle.Text = count ? Translator.SemanticIndex_CoverageNewestCount : Translator.SemanticIndex_CoveragePeriod;
            RebuildPresets(presetFlow, count);
        });
        Watch(s => (s.LatestCount, s.RangeMaximum, s.RangeStartOffset, s.RangeEndOffset, s.SelectedFolder?.Rule), _ => UpdatePresetTint());
        var presets = Stack(Resource.Dimension.wino_space_sm, presetTitle, presetFlow);

        // 3. Histogram, slider or range selector, edge labels.
        Watch(s => s.IsHistogramVisible, on => Show(histogram, on));
        var slider = WinoMaterialAccent.Tint(new Slider(context) { ValueFrom = 0, ValueTo = 1, ContentDescription = Translator.SemanticIndex_CoverageNewestCount });
        var range = new RangeSlider(context) { ValueFrom = 0, ValueTo = 1 };
        range.SetValues(Java.Lang.Float.ValueOf(0), Java.Lang.Float.ValueOf(1));
        TintRange(range);
        bool applying = false;
        Watch(s => (s.RangeMaximum, s.LatestCount, s.RangeStartOffset, s.RangeEndOffset, s.IsLatestCountMode), state =>
        {
            applying = true;
            try
            {
                var maximum = (float)Math.Max(1, state.RangeMaximum);
                // Material sliders reject a value outside their range, so the range moves first.
                slider.Value = 0;
                slider.ValueTo = maximum;
                slider.Value = Math.Clamp((float)state.LatestCount, 0, maximum);
                range.SetValues(Java.Lang.Float.ValueOf(0), Java.Lang.Float.ValueOf(0));
                range.ValueTo = maximum;
                var start = Math.Clamp((float)state.RangeStartOffset, 0, maximum);
                var end = Math.Clamp((float)state.RangeEndOffset, start, maximum);
                range.SetValues(Java.Lang.Float.ValueOf(start), Java.Lang.Float.ValueOf(end));
                Show(slider, state.IsLatestCountMode);
                Show(range, !state.IsLatestCountMode);
            }
            finally { applying = false; }
        });
        var sliderListener = new SliderChange(value =>
        {
            if (!applying) vm.LatestCount = Math.Round(value);
        });
        slider.AddOnChangeListener(sliderListener);
        var rangeListener = new RangeChange(values =>
        {
            if (applying || values.Count < 2) return;
            vm.RangeStartOffset = Math.Round(values[0]);
            vm.RangeEndOffset = Math.Round(values[1]);
        });
        range.AddOnChangeListener(rangeListener);
        Bindings.Own(new ActionDisposable(() =>
        {
            slider.RemoveOnChangeListener(sliderListener);
            range.RemoveOnChangeListener(rangeListener);
        }));
        var newest = Text(Translator.SemanticIndex_CoverageNewestEdge, WinoTextStyle.Caption);
        var reach = Text(string.Empty, WinoTextStyle.BodyStrong);
        reach.Gravity = GravityFlags.End;
        Watch(s => s.ReachSummary, text =>
        {
            reach.Text = text;
            range.ContentDescription = text;
        });
        var edges = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_m, reach, newest, reach);
        var chart = Stack(Resource.Dimension.wino_space_xs, histogram, slider, range, edges);

        // 4. Bands.
        var bands = new LinearLayout(context) { Orientation = Orientation.Horizontal, WeightSum = 3 };
        View[] tiles =
        [
            Band(Translator.SemanticIndex_CoverageBandIndexed, CoverageColors.Indexed(context), s => s.FolderIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandToIndex, CoverageColors.ToIndex(context), s => s.FolderSelectedNotIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandOutside, CoverageColors.Outside(context), s => s.FolderOutsideCount)
        ];
        foreach (var tile in tiles)
            bands.AddView(tile, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1) { MarginStart = bands.ChildCount > 0 ? WinoTokens.Px(context, Resource.Dimension.wino_space_s) : 0 });

        var editor = Stack(Resource.Dimension.wino_space_xl, mode, presets, chart, bands);
        Watch(s => s.IsEditorVisible, on => Show(editor, on));

        var stack = Stack(Resource.Dimension.wino_space_ml, top, IntelligenceViews.Separator(context), notice, editor);
        return IntelligenceViews.Surface(context, stack, Resource.Dimension.wino_space_l);
    }

    private TextView SectionTitle(string text)
    {
        var label = Text(text, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) label.AccessibilityHeading = true;
        return label;
    }

    private View Band(string name, Color color, Func<IntelligenceCoveragePageViewModel, int> count)
    {
        var context = Ctx;
        var dot = new View(context) { Background = IntelligenceViews.Oval(context, color) };
        int dotSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_band_dot);
        dot.LayoutParameters = new LinearLayout.LayoutParams(dotSize, dotSize);
        var label = Text(name, WinoTextStyle.Caption, maxLines: 2);
        var caption = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_xs, label, dot, label);
        var value = Text(string.Empty, WinoTextStyle.Subtitle);
        var stack = Stack(Resource.Dimension.wino_space_2xs, caption, value);
        Watch(count, n =>
        {
            value.Text = n.ToString("N0", CultureInfo.CurrentCulture);
            stack.ContentDescription = $"{name}, {value.Text}";
        });
        stack.ImportantForAccessibility = ImportantForAccessibility.Yes;
        caption.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        value.ImportantForAccessibility = ImportantForAccessibility.No;
        stack.Background = IntelligenceViews.Shape(context, WinoColors.SubtleFill(context), null, Resource.Dimension.wino_radius_small);
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_sm);
        return stack;
    }

    private void RebuildPresets(Google.Android.Material.Chip.ChipGroup flow, bool count)
    {
        flow.RemoveAllViews();
        _presetButtons.Clear();
        var vm = VM;
        if (count)
        {
            foreach (var preset in vm.CountPresets)
                AddPreset(flow, preset.DisplayName, () => vm.ApplyCountPresetCommand.Execute(preset),
                    () => vm.IsLatestCountMode && Math.Abs(vm.LatestCount - Math.Min(preset.Count, vm.RangeMaximum)) < 0.5 && vm.LatestCount > 0);
        }
        else
        {
            foreach (var preset in vm.DatePresets)
                AddPreset(flow, preset.DisplayName, () => vm.ApplyDatePresetCommand.Execute(preset),
                    () => vm.IsDateRangeMode && vm.SelectedFolder?.Rule is { Mode: SemanticIndexCoverageMode.DateRange } rule && rule.DatePreset == preset.Preset);
        }
        UpdatePresetTint();
    }

    private void AddPreset(Google.Android.Material.Chip.ChipGroup flow, string title, Action action, Func<bool> isActive)
    {
        var button = SettingsBinder.CreateButton(Ctx, title);
        int minimumWidth = WinoTokens.Px(Ctx, Resource.Dimension.wino_winoaccount_preset_min_width);
        button.SetMinWidth(minimumWidth);
        button.SetMinimumWidth(minimumWidth);
        Bind.Views.Click(button, action);
        flow.AddView(button, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _presetButtons.Add((button, isActive));
    }

    /// <summary>The preset matching the current rule is tinted with the accent.</summary>
    private void UpdatePresetTint()
    {
        if (Bindings.IsDisposed) return;
        var context = Ctx;
        foreach (var (button, isActive) in _presetButtons)
        {
            bool active;
            try { active = isActive(); }
            catch (Exception exception)
            {
                ReportError(exception);
                active = false;
            }
            button.BackgroundTintList = ColorStateList.ValueOf(active ? WinoColors.SelectedFill(context) : WinoColors.SubtleFill(context));
            button.StrokeColor = ColorStateList.ValueOf(active ? WinoColors.Accent(context) : WinoColors.CardStroke(context));
            button.SetTextColor(active ? WinoColors.AccentText(context) : WinoColors.PrimaryText(context));
            button.Selected = active;
        }
    }

    /// <summary>Rebuilds the flattened tree (every folder expanded) from the ViewModel's roots.</summary>
    private void ReloadTree(LinearLayout rows)
    {
        rows.RemoveAllViews();
        _cells.Clear();
        void AddNodes(IEnumerable<IntelligenceFolderNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var cell = new CoverageFolderCell(Ctx, node, depth);
                EventHandler picked = (_, _) => Pick(node);
                cell.Picked += picked;
                Bindings.Own(new ActionDisposable(() => cell.Picked -= picked));
                _cells.Add(cell);
                AddTo(rows, cell, 0);
                if (node.HasChildren) AddNodes(node.ChildNodes, depth + 1);
            }
        }
        AddNodes(VM.RootFolders.ToList(), 0);
        RefreshRows();
    }

    private void Pick(IntelligenceFolderNode node)
    {
        try
        {
            if (VM.SelectFolderCommand.CanExecute(node)) VM.SelectFolderCommand.Execute(node);
        }
        catch (Exception exception) { ReportError(exception); }
        RefreshRows();
    }

    private void RefreshRows()
    {
        var selected = ViewModel?.SelectedFolder;
        foreach (var cell in _cells) cell.Configure(ReferenceEquals(cell.Node, selected));
    }

    private void TintRange(RangeSlider range)
    {
        var accent = ColorStateList.ValueOf(WinoColors.Accent(Ctx));
        range.TrackActiveTintList = accent;
        range.ThumbTintList = accent;
        range.HaloTintList = ColorStateList.ValueOf(WinoColors.WithAlpha(WinoColors.Accent(Ctx), 0.2));
    }

    private sealed class SliderChange(Action<float> changed) : Java.Lang.Object, Slider.IOnChangeListener
    {
        public void OnValueChange(Slider slider, float value, bool fromUser)
        {
            if (fromUser) changed(value);
        }
    }

    private sealed class RangeChange(Action<IReadOnlyList<float>> changed) : Java.Lang.Object, RangeSlider.IOnChangeListener
    {
        public void OnValueChange(RangeSlider slider, float value, bool fromUser)
        {
            if (!fromUser) return;
            var values = slider.Values;
            changed(values is null ? [] : values.Select(static item => item.FloatValue()).ToList());
        }
    }
}
