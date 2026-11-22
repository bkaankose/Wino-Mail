using System.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Sender rules (Windows RemoteContentRulesPage): add a rule for an address or a domain, the policy legend, the
/// spoofing note, count, policy filter and search, one row per rule with its policy and Delete, the empty state and
/// the footer.
/// </summary>
internal sealed class RemoteContentRulesPage(RemoteContentRulesPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<RemoteContentRulesPageViewModel>(viewModel, services)
{
    // A boxed list of our own rather than the group's: the group can only append, and a change to one rule must not
    // rebuild every row (300 rows took 417 ms). Rows are keyed by their ViewModel, which the ViewModel reuses across
    // filters and single-rule refreshes; a row the filter hides is kept for the page's lifetime, so clearing a search
    // only re-inserts rows (a deleted rule's row stays cached until the page is released).
    private readonly Gtk.ListBox _rules = Gtk.ListBox.New();
    private readonly List<RemoteContentRuleViewModel> _shown = [];
    private readonly Dictionary<RemoteContentRuleViewModel, (Adw.ActionRow Row, SettingsBinder Binder)> _rows = new(ReferenceEqualityComparer.Instance);
    private MainLoop.Coalescer? _continue;
    private Stopwatch? _pending;

    // A row costs about a millisecond to build, so a long list is built a screenful per main-loop turn: the first
    // rows draw at once and the rest follow while the page already scrolls.
    private const int RowsPerTurn = 24;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var pattern = Settings.TextSuffix(vm, Translator.RemoteContentRules_PatternPlaceholder, Translator.RemoteContentRules_PatternPlaceholder,
            nameof(vm.NewPattern), static s => s.NewPattern, static (s, v) => s.NewPattern = v);
        pattern.SetHexpand(true);
        Settings.Own(GtkSignals.Connect<Gtk.Entry>(h => pattern.OnActivate += h, h => pattern.OnActivate -= h, (_, _) =>
        {
            if (vm.AddRuleCommand.CanExecute(null)) vm.AddRuleCommand.Execute(null);
        }));
        var policy = Settings.ChoiceSuffix<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, Translator.RemoteContentRules_ColumnPolicy,
            static s => s.PolicyOptions, static option => option.Title,
            nameof(vm.NewPolicyOption), static s => s.NewPolicyOption, static (s, v) => s.NewPolicyOption = v);
        var entry = Adw.WrapBox.New();
        entry.SetChildSpacing(8);
        entry.SetLineSpacing(8);
        entry.Append(pattern);
        entry.Append(policy);
        entry.Append(Settings.Button(Translator.RemoteContentRules_AddRule, vm.AddRuleCommand, suggested: true));

        var legend = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        foreach (var option in vm.PolicyOptions)
        {
            var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            line.Append(PolicyDot(option.Policy));
            var title = Gtk.Label.New(option.Title);
            title.AddCssClass("heading");
            line.Append(title);
            var description = WinoSettingsRows.Caption(option.Legend);
            description.SetHexpand(true);
            line.Append(description);
            legend.Append(line);
        }

        AddGroup(null,
            WinoSettingsRows.Custom(entry, Translator.RemoteContentRules_AddRule),
            WinoSettingsRows.Custom(legend),
            WinoSettingsRows.Custom(WinoSettingsRows.Caption(Translator.RemoteContentRules_SpoofingInfo)));

        var count = Gtk.Label.New(string.Empty);
        count.AddCssClass("heading");
        Settings.Bind(vm, nameof(vm.CountText), static s => s.CountText, text => count.SetText(text ?? string.Empty));
        var filter = Settings.ChoiceSuffix<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, Translator.RemoteContentRules_AllPolicies,
            static s => s.PolicyFilterOptions, static option => option.Title,
            nameof(vm.SelectedPolicyFilterOption), static s => s.SelectedPolicyFilterOption, static (s, v) => s.SelectedPolicyFilterOption = v);
        var search = Gtk.SearchEntry.New();
        search.SetPlaceholderText(Translator.RemoteContentRules_SearchPlaceholder);
        search.SetHexpand(true);
        search.SetHalign(Gtk.Align.End);
        search.SetWidthChars(18);
        WinoA11y.Label(search, Translator.RemoteContentRules_SearchPlaceholder);
        Settings.Bind(vm, nameof(vm.Filter), static s => s.Filter, value =>
        {
            var text = value ?? string.Empty;
            if (search.GetText() != text) search.SetText(text);
        });
        Settings.Own(GtkSignals.OnChanged(search, () => vm.Filter = search.GetText()));
        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        toolbar.Append(count);
        toolbar.Append(filter);
        toolbar.Append(search);
        Settings.Visible(AddGroup(null, WinoSettingsRows.Custom(toolbar)).Root, vm, nameof(vm.HasRules), static s => s.HasRules);

        _rules.AddCssClass("boxed-list");
        _rules.SetSelectionMode(Gtk.SelectionMode.None);
        _rules.SetVisible(false);
        var rules = Adw.PreferencesGroup.New();
        rules.Add(_rules);
        Page.Page.Add(rules);
        _continue = new MainLoop.Coalescer(Rebuild);
        Settings.Own(_continue);
        Settings.Collection(vm.Rules, Rebuild);

        var empty = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        empty.SetMarginTop(24);
        empty.SetMarginBottom(24);
        var shield = new WinoIcon(WinoIconGlyph.Shield, 32).Root;
        shield.AddCssClass("dim-label");
        empty.Append(shield);
        var emptyTitle = Gtk.Label.New(Translator.RemoteContentRules_EmptyTitle);
        emptyTitle.AddCssClass("heading");
        empty.Append(emptyTitle);
        var emptyDescription = WinoSettingsRows.Caption(Translator.RemoteContentRules_EmptyDescription);
        emptyDescription.SetXalign(0.5f);
        emptyDescription.SetJustify(Gtk.Justification.Center);
        empty.Append(emptyDescription);

        var showing = WinoSettingsRows.Caption(null);
        showing.SetHexpand(true);
        Settings.Bind(vm, nameof(vm.ShowingText), static s => s.ShowingText, text => showing.SetText(text ?? string.Empty));
        var showAll = Settings.Button(Translator.RemoteContentRules_ShowAll, vm.ShowAllCommand);
        showAll.AddCssClass("flat");
        var subset = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        subset.Append(showing);
        subset.Append(showAll);

        AddGroup(null,
            Settings.Visible(WinoSettingsRows.Custom(WinoSettingsRows.Caption(Translator.RemoteContentRules_NoMatches)), vm, nameof(vm.HasNoMatches), static s => s.HasNoMatches),
            Settings.Visible(WinoSettingsRows.Custom(empty, Translator.RemoteContentRules_EmptyTitle), vm, nameof(vm.IsEmpty), static s => s.IsEmpty),
            Settings.Visible(WinoSettingsRows.Custom(subset), vm, nameof(vm.IsShowingSubset), static s => s.IsShowingSubset),
            WinoSettingsRows.Custom(WinoSettingsRows.Caption(Translator.RemoteContentRules_Footer)));
    }

    /// <summary>
    /// Takes out the rows that left the list, creates the new ones and moves the rest into the list's order. It creates
    /// at most <see cref="RowsPerTurn"/> rows and asks for another turn for the rest.
    /// </summary>
    private void Rebuild()
    {
        var watch = Stopwatch.StartNew();
        _pending ??= Stopwatch.StartNew();
        var target = ViewModel.Rules.ToList();
        var keep = new HashSet<RemoteContentRuleViewModel>(target, ReferenceEqualityComparer.Instance);
        var created = 0;
        for (var index = _shown.Count - 1; index >= 0; index--)
        {
            var rule = _shown[index];
            if (keep.Contains(rule)) continue;
            _rules.Remove(_rows[rule].Row);
            _shown.RemoveAt(index);
        }

        for (var index = 0; index < target.Count; index++)
        {
            var rule = target[index];
            if (index < _shown.Count && ReferenceEquals(_shown[index], rule)) continue;
            if (_rows.TryGetValue(rule, out var entry))
            {
                _rules.Remove(entry.Row);
                _shown.Remove(rule);
            }
            else
            {
                if (created == RowsPerTurn)
                {
                    _continue?.Request();
                    break;
                }
                var binder = Settings.Child();
                entry = (RuleRow(binder, rule), binder);
                _rows[rule] = entry;
                created++;
            }
            _rules.Insert(entry.Row, index);
            _shown.Insert(index, rule);
        }

        _rules.SetVisible(_shown.Count > 0);
        var complete = _shown.Count == target.Count;
        Serilog.Log.Debug("Sender rules page updated {Count} of {Total} rows ({Created} new) in {Elapsed} ms{Done}.", _shown.Count, target.Count, created,
            watch.ElapsedMilliseconds, complete ? $", complete after {_pending.ElapsedMilliseconds} ms" : string.Empty);
        if (complete) _pending = null;
    }

    private static Adw.ActionRow RuleRow(SettingsBinder binder, RemoteContentRuleViewModel rule)
    {
        var row = WinoSettingsRows.Action(rule.DisplayName);
        binder.Bind(rule, nameof(rule.DisplayName), static r => r.DisplayName, name => row.SetTitle(name ?? string.Empty));
        binder.Bind(rule, nameof(rule.DetailText), static r => RuleDescription(r), text => row.SetSubtitle(text));

        if (rule.IsDomain)
        {
            var globe = new WinoIcon(WinoIconGlyph.Globe, 16).Root;
            var circle = Adw.Bin.New();
            circle.SetChild(globe);
            circle.SetSizeRequest(32, 32);
            circle.AddCssClass(WinoCssClass.Chip);
            circle.SetValign(Gtk.Align.Center);
            row.AddPrefix(circle);
        }
        else
        {
            var avatar = new WinoContactPicture(32);
            avatar.SetIdentity(rule.DisplayName, rule.Pattern);
            avatar.Root.SetValign(Gtk.Align.Center);
            row.AddPrefix(avatar.Root);
        }

        var kind = Gtk.Label.New(rule.KindText);
        kind.AddCssClass(WinoCssClass.Chip);
        kind.AddCssClass("caption");
        WinoSettingsRows.Suffix(row, kind);
        var dot = WinoSettingsRows.Suffix(row, PolicyDot(rule.Policy));
        binder.Bind(rule, nameof(rule.SelectedPolicyOption), static r => r.SelectedPolicyOption?.Policy ?? r.Policy, policy => SetPolicyTint(dot, policy));
        WinoSettingsRows.Suffix(row, binder.ChoiceSuffix<RemoteContentRuleViewModel, RemoteContentPolicyOption>(rule, rule.PolicyAutomationName,
            static r => r.PolicyOptions, static option => option.Title,
            nameof(rule.SelectedPolicyOption), static r => r.SelectedPolicyOption, static (r, v) => r.SelectedPolicyOption = v));
        var delete = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, rule.DeleteAutomationName));
        binder.Own(GtkSignals.OnClicked(delete, () =>
        {
            if (rule.RemoveCommand.CanExecute(null)) rule.RemoveCommand.Execute(null);
        }));
        return row;
    }

    private static string RuleDescription(RemoteContentRuleViewModel rule)
        => rule.HasDetailText ? $"{rule.DetailText}\n{rule.AddedText}" : rule.AddedText;

    /// <summary>The policy colour; the policy is always named in text next to it.</summary>
    private static Gtk.Label PolicyDot(RemoteContentSenderPolicy? policy)
    {
        var dot = Gtk.Label.New("●");
        dot.AccessibleRole = Gtk.AccessibleRole.Presentation;
        SetPolicyTint(dot, policy);
        return dot;
    }

    private static void SetPolicyTint(Gtk.Label dot, RemoteContentSenderPolicy? policy)
    {
        dot.RemoveCssClass("success");
        dot.RemoveCssClass("warning");
        dot.RemoveCssClass("error");
        dot.SetVisible(policy is not null);
        dot.AddCssClass(policy switch
        {
            RemoteContentSenderPolicy.LoadEverything => "success",
            RemoteContentSenderPolicy.ImagesOnly => "warning",
            _ => "error",
        });
    }
}
