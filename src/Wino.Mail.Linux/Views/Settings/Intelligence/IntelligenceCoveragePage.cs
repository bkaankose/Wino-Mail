using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Controls.Linux.Intelligence;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// The coverage editor (Windows IntelligenceCoveragePage, macOS IntelligenceCoveragePageViewController): totals with Cancel
/// and Apply, the folder tree, and the selected folder's rule: mode, presets, the histogram with the count slider or the
/// date range, and the three band counts. Settings content is at most 600 px wide, so the tree sits above the editor.
/// </summary>
internal sealed partial class IntelligenceCoveragePage(IntelligenceCoveragePageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<IntelligenceCoveragePageViewModel>(viewModel, services)
{
    private readonly List<(Gtk.Button Button, Func<bool> IsActive)> _presetButtons = [];
    private readonly Adw.WrapBox _presets = Adw.WrapBox.New();
    private WinoCoverageHistogram? _histogram;
    private WinoCoverageRange? _range;
    private Gtk.Scale? _latest;
    private bool _applying;
    private Gtk.Widget? _editorCard;
    private WinoSettingsGroup? _headerGroup;
    private WinoSettingsGroup? _foldersGroup;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var header = _headerGroup = Page.AddGroup();
        header.HidesWhenEmpty = false;
        header.Root.Add(Header());

        var folders = _foldersGroup = Page.AddGroup(Translator.SemanticIndex_CoverageIncludedFolders);
        folders.HidesWhenEmpty = false;
        folders.Root.Add(FolderPanel());

        var editor = Page.AddGroup();
        editor.HidesWhenEmpty = false;
        editor.Root.Add(_editorCard = EditorPanel());

        Bind.BindCollection(vm.RootFolders, RebuildTree);
        Bind.Bind(nameof(vm.SelectedFolder), static s => s.SelectedFolder, _ => RefreshTree());
        Bind.Bind(nameof(vm.TotalSummary), static s => s.TotalSummary, _ => RefreshTree());
        Bind.BindCollection(vm.Buckets, () => _histogram?.SetBuckets([.. vm.Buckets.Select(static b => new WinoCoverageBucket(b.IndexedHeight, b.SelectedNotIndexedHeight, b.OutsideHeight, b.Tooltip))]));
#if DEBUG
        Current = this;
#endif
    }

    // A fresh ViewModel on Back must still load the editor arguments.
    protected override Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(NavigationMode.New, parameter!);
        return Task.CompletedTask;
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(Current, this)) Current = null;
#endif
        return base.DeactivateAsync();
    }

    private Gtk.Widget Header()
    {
        var vm = ViewModel;
        var totals = Gtk.Label.New(string.Empty);
        totals.SetXalign(0);
        totals.SetHexpand(true);
        totals.SetWrap(true);
        totals.SetWrapMode(Pango.WrapMode.WordChar);
        Bind.BindAny(static s => (s.TotalSummary, s.ScopeSummary), state =>
        {
            var total = GLib.Functions.MarkupEscapeText(state.TotalSummary ?? string.Empty, -1);
            var scope = GLib.Functions.MarkupEscapeText(state.ScopeSummary ?? string.Empty, -1);
            totals.SetMarkup($"<b>{total}</b> <span alpha=\"60%\">· {scope}</span>");
            WinoA11y.Label(totals, $"{state.TotalSummary} · {state.ScopeSummary}");
        });
        var cancel = WinoA11y.Rename(Settings.Button(Translator.Buttons_Cancel, vm.CancelCommand), Translator.Buttons_Cancel);
        var apply = WinoA11y.Rename(Settings.Button(Translator.Buttons_Apply, vm.ApplyCommand, suggested: true), Translator.Buttons_Apply);
        Bind.BindAny(static s => s.CanApply, apply.SetSensitive);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.Append(totals);
        foreach (var button in (Gtk.Widget[])[cancel, apply])
        {
            button.SetValign(Gtk.Align.Center);
            row.Append(button);
        }
        return row;
    }

    private Gtk.Widget EditorPanel()
    {
        var vm = ViewModel;
        var name = Gtk.Label.New(string.Empty);
        name.AddCssClass("title-4");
        name.SetXalign(0);
        name.SetHexpand(true);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        Bind.BindAny(static s => s.SelectedFolderName, name.SetText);
        var applyAll = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_CoverageApplyToAll, vm.ApplyToAllFoldersCommand), Translator.SemanticIndex_CoverageApplyToAll);
        var clear = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_CoverageClearFolder, vm.ClearFolderCommand), Translator.SemanticIndex_CoverageClearFolder);
        clear.SetTooltipText(Translator.SemanticIndex_CoverageClearFolderTooltip);
        Bind.BindAny(static s => s.IsEditorVisible, on =>
        {
            applyAll.SetSensitive(on);
            clear.SetSensitive(on);
        });
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        top.Append(name);
        top.Append(applyAll);
        top.Append(clear);

        var notice = IntelligenceViews.Text(Translator.SemanticIndex_CoverageFolderEmptyNotice, "dim-label");
        Bind.BindAny(static s => s.IsEmptyFolderNoticeVisible, notice.SetVisible);

        var editor = Gtk.Box.New(Gtk.Orientation.Vertical, 20);
        editor.Append(Section(Translator.SemanticIndex_CoverageModeTitle, ModeSwitch()));
        var presetTitle = Gtk.Label.New(string.Empty);
        presetTitle.AddCssClass("heading");
        presetTitle.SetXalign(0);
        _presets.SetChildSpacing(8);
        _presets.SetLineSpacing(8);
        Bind.BindAny(static s => s.IsLatestCountMode, count =>
        {
            presetTitle.SetText(count ? Translator.SemanticIndex_CoverageNewestCount : Translator.SemanticIndex_CoveragePeriod);
            RebuildPresets(count);
        });
        Bind.BindAny(static s => (s.LatestCount, s.RangeMaximum, s.RangeStartOffset, s.RangeEndOffset, s.SelectedFolder?.Rule), _ => UpdatePresets());
        var presets = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        presets.Append(presetTitle);
        presets.Append(_presets);
        editor.Append(presets);
        editor.Append(Chart());
        editor.Append(Bands());
        Bind.BindAny(static s => s.IsEditorVisible, editor.SetVisible);

        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        stack.AddCssClass("card");
        foreach (var part in (Gtk.Widget[])[top, notice, editor])
        {
            part.SetMarginStart(20);
            part.SetMarginEnd(20);
            stack.Append(part);
        }
        top.SetMarginTop(16);
        editor.SetMarginBottom(20);
        notice.SetMarginBottom(16);
        return stack;
    }

    private static Gtk.Widget Section(string title, Gtk.Widget content)
    {
        var label = Gtk.Label.New(title);
        label.AddCssClass("heading");
        label.SetXalign(0);
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        stack.Append(label);
        stack.Append(content);
        return stack;
    }

    /// <summary>Latest count or date range as two linked toggle buttons (assistive technology cannot toggle GTK radio check buttons).</summary>
    private Gtk.Widget ModeSwitch()
    {
        var vm = ViewModel;
        var latest = Gtk.ToggleButton.NewWithLabel(Translator.SemanticIndex_CoverageModeLatestCount);
        var range = Gtk.ToggleButton.NewWithLabel(Translator.SemanticIndex_CoverageModeDateRange);
        range.SetGroup(latest);
        WinoA11y.Label(latest, Translator.SemanticIndex_CoverageModeLatestCount);
        WinoA11y.Label(range, Translator.SemanticIndex_CoverageModeDateRange);
        Bind.BindAny(static s => s.CoverageModeIndex, index =>
        {
            _applying = true;
            try
            {
                latest.SetActive(index == 0);
                range.SetActive(index == 1);
            }
            finally { _applying = false; }
        });
        Settings.Own(GtkSignals.OnToggled(latest, () =>
        {
            if (!_applying && latest.GetActive()) vm.CoverageModeIndex = 0;
        }));
        Settings.Own(GtkSignals.OnToggled(range, () =>
        {
            if (!_applying && range.GetActive()) vm.CoverageModeIndex = 1;
        }));
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        box.AddCssClass("linked");
        box.SetHalign(Gtk.Align.Start);
        box.Append(latest);
        box.Append(range);
        return box;
    }

    private Gtk.Widget Chart()
    {
        var vm = ViewModel;
        var histogram = _histogram = new WinoCoverageHistogram((int)Wino.Mail.ViewModels.Data.SemanticIndexRangeBucketViewModel.MaximumBarHeight);
        Settings.Own(histogram);
        Bind.BindAny(static s => s.IsHistogramVisible, histogram.Root.SetVisible);

        var latest = _latest = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, 1, 1);
        latest.SetDrawValue(false);
        latest.SetDigits(0);
        latest.SetHexpand(true);
        WinoA11y.LabelWithTooltip(latest, Translator.SemanticIndex_CoverageNewestCount);
        var range = _range = new WinoCoverageRange();
        Settings.Own(range);
        range.SetAccessibleNames(Translator.SemanticIndex_CoverageNewestEdge, Translator.SemanticIndex_CoveragePeriod);
        Bind.BindAny(static s => (s.RangeMaximum, s.LatestCount, s.RangeStartOffset, s.RangeEndOffset, s.IsLatestCountMode), state =>
        {
            _applying = true;
            try
            {
                var maximum = Math.Max(1, state.RangeMaximum);
                latest.SetRange(0, maximum);
                latest.SetIncrements(1, Math.Max(1, Math.Round(maximum / 10)));
                latest.SetValue(state.LatestCount);
                range.SetRange((int)Math.Round(state.RangeMaximum), (int)Math.Round(state.RangeStartOffset), (int)Math.Round(state.RangeEndOffset));
            }
            finally { _applying = false; }
            latest.SetVisible(state.IsLatestCountMode);
            range.Root.SetVisible(!state.IsLatestCountMode);
        });
        Settings.Own(GtkSignals.Connect<Gtk.Range>(h => latest.OnValueChanged += h, h => latest.OnValueChanged -= h, (_, _) =>
        {
            if (!_applying) vm.LatestCount = Math.Round(latest.GetValue());
        }));
        range.Changed += (start, end) =>
        {
            vm.RangeStartOffset = start;
            vm.RangeEndOffset = end;
        };

        var newest = Gtk.Label.New(Translator.SemanticIndex_CoverageNewestEdge);
        newest.AddCssClass("caption");
        newest.AddCssClass("dim-label");
        var reach = IntelligenceViews.Text(null, "heading");
        reach.SetXalign(1);
        reach.SetHexpand(true);
        Bind.BindAny(static s => s.ReachSummary, text => reach.SetText(text ?? string.Empty));
        var edges = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        edges.Append(newest);
        edges.Append(reach);

        var chart = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        chart.Append(histogram.Root);
        chart.Append(latest);
        chart.Append(range.Root);
        chart.Append(edges);
        return chart;
    }

    private Gtk.Widget Bands()
    {
        var bands = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        bands.SetHomogeneous(true);
        bands.Append(Band(Translator.SemanticIndex_CoverageBandIndexed, "wino-coverage-indexed", static s => s.FolderIndexedCount));
        bands.Append(Band(Translator.SemanticIndex_CoverageBandToIndex, "wino-coverage-to-index", static s => s.FolderSelectedNotIndexedCount));
        bands.Append(Band(Translator.SemanticIndex_CoverageBandOutside, "wino-coverage-outside", static s => s.FolderOutsideCount));
        return bands;
    }

    private Gtk.Widget Band(string name, string dotClass, Func<IntelligenceCoveragePageViewModel, int> count)
    {
        var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        dot.AddCssClass("wino-coverage-dot");
        dot.AddCssClass(dotClass);
        dot.SetSizeRequest(8, 8);
        dot.SetValign(Gtk.Align.Center);
        var label = Gtk.Label.New(name);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        label.SetEllipsize(Pango.EllipsizeMode.End);
        var title = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        title.Append(dot);
        title.Append(label);
        var value = Gtk.Label.New(string.Empty);
        value.AddCssClass("title-4");
        value.SetXalign(0);
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        stack.AddCssClass("wino-coverage-band");
        stack.Append(title);
        stack.Append(value);
        Bind.BindAny(count, n =>
        {
            value.SetText(n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
            WinoA11y.Label(stack, $"{name}: {value.GetText()}");
        });
        return stack;
    }

    private void RebuildPresets(bool count)
    {
        while (_presets.GetFirstChild() is { } child) _presets.Remove(child);
        _presetButtons.Clear();
        var vm = ViewModel;
        if (count)
        {
            foreach (var preset in vm.CountPresets)
                AddPreset(preset.DisplayName, () => vm.ApplyCountPresetCommand.Execute(preset),
                    () => vm.IsLatestCountMode && Math.Abs(vm.LatestCount - Math.Min(preset.Count, vm.RangeMaximum)) < 0.5 && vm.LatestCount > 0);
        }
        else
        {
            foreach (var preset in vm.DatePresets)
                AddPreset(preset.DisplayName, () => vm.ApplyDatePresetCommand.Execute(preset),
                    () => vm.IsDateRangeMode && vm.SelectedFolder?.Rule is { Mode: SemanticIndexCoverageMode.DateRange } rule && rule.DatePreset == preset.Preset);
        }
        UpdatePresets();
    }

    private void AddPreset(string title, Action run, Func<bool> isActive)
    {
        var button = WinoA11y.Rename(Gtk.Button.NewWithLabel(title), title);
        button.AddCssClass("wino-coverage-preset");
        Settings.Own(GtkSignals.OnClicked(button, run));
        _presets.Append(button);
        _presetButtons.Add((button, isActive));
    }

    /// <summary>The preset that matches the current rule is tinted with the accent.</summary>
    private void UpdatePresets()
    {
        foreach (var (button, isActive) in _presetButtons)
        {
            if (isActive()) button.AddCssClass("wino-coverage-preset-active");
            else button.RemoveCssClass("wino-coverage-preset-active");
        }
    }

#if DEBUG
    internal static IntelligenceCoveragePage? Current { get; private set; }

    internal string Describe()
    {
        var vm = ViewModel;
        var active = string.Join("|", _presetButtons.Where(static p => p.Button.HasCssClass("wino-coverage-preset-active")).Select(static p => p.Button.GetLabel()));
        return $"folder='{vm.SelectedFolderName}' editor={vm.IsEditorVisible} mode={vm.CoverageModeIndex} latest={vm.LatestCount} range={vm.RangeStartOffset}-{vm.RangeEndOffset}/{vm.RangeMaximum} "
            + $"buckets={_histogram?.Count} histogram={vm.IsHistogramVisible} reach='{vm.ReachSummary}' total='{vm.TotalSummary}' bands={vm.FolderIndexedCount}/{vm.FolderSelectedNotIndexedCount}/{vm.FolderOutsideCount} "
            + $"presets={_presetButtons.Count} active='{active}' tree={_treeButtons.Count} canApply={vm.CanApply}";
    }

    internal void DebugCapture(string path)
    {
        if (_editorCard is { } card) Infrastructure.Diagnostics.DebugActions.Capture(card, path);
    }

    /// <summary>Hides the totals and the tree so the whole editor fits the window for a screenshot.</summary>
    internal void DebugEditorOnly(bool on)
    {
        _headerGroup?.Root.SetVisible(!on);
        _foldersGroup?.Root.SetVisible(!on);
    }

    internal void DebugSetRange(int start, int end)
    {
        ViewModel.RangeStartOffset = start;
        ViewModel.RangeEndOffset = end;
    }

    internal void DebugSelect(int index)
    {
        if (index >= 0 && index < _treeButtons.Count) _treeButtons[index].Button.Activate();
    }

    internal void DebugPreset(int index)
    {
        if (index >= 0 && index < _presetButtons.Count) _presetButtons[index].Button.Activate();
    }
#endif
}
