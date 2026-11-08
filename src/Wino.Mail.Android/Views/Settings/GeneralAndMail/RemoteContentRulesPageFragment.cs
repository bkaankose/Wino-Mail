using System.ComponentModel;
using Android.Views;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Views.Settings.Accounts;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Sender rules (Windows RemoteContentRulesPage): the add form, the search and policy filter, then one row per rule
/// with its policy and an actions menu that changes the policy or deletes the rule. The Windows table columns become
/// the row's supporting text; the policy ComboBox becomes checked menu entries.
/// </summary>
public sealed class RemoteContentRulesPageFragment : SettingsPageFragment<RemoteContentRulesPageViewModel>
{
    private WinoSettingsGroup? _rules;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));

        var add = Group(Translator.RemoteContentRules_AddDialogTitle,
            StackedCard(Translator.RemoteContentRules_ColumnSender, Translator.RemoteContentRules_PatternPlaceholder, WinoIconGlyph.None,
                Bind.TextField(vm, nameof(vm.NewPattern), s => s.NewPattern, (s, v) => s.NewPattern = v, Translator.RemoteContentRules_PatternPlaceholder)),
            StackedCard(Translator.RemoteContentRules_ColumnPolicy, null, WinoIconGlyph.None,
                Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyOptions, option => option.Title,
                    nameof(vm.NewPolicyOption), s => s.NewPolicyOption, (s, v) => s.NewPolicyOption = v)),
            ActionCard(Translator.RemoteContentRules_AddRule, vm.AddRuleCommand));
        add.Footer = Translator.RemoteContentRules_SpoofingInfo;
        Add(add);

        var filters = Group(vm.CountText,
            StackedCard(Translator.RemoteContentRules_SearchPlaceholder, null, WinoIconGlyph.None,
                Bind.TextField(vm, nameof(vm.Filter), s => s.Filter, (s, v) => s.Filter = v, Translator.RemoteContentRules_SearchPlaceholder)),
            StackedCard(Translator.RemoteContentRules_ColumnPolicy, null, WinoIconGlyph.None,
                Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyFilterOptions, option => option.Title,
                    nameof(vm.SelectedPolicyFilterOption), s => s.SelectedPolicyFilterOption, (s, v) => s.SelectedPolicyFilterOption = v)));
        Bind.Bind(vm, nameof(vm.CountText), s => s.CountText, text => filters.Title = text);
        Bind.Visible(Add(filters), vm, nameof(vm.HasRules), s => s.HasRules);

        var empty = Group(null, Card(Translator.RemoteContentRules_EmptyTitle, null));
        empty.Footer = Translator.RemoteContentRules_EmptyDescription;
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmpty), s => s.IsEmpty);

        var noMatches = Group(null, Card(Translator.RemoteContentRules_NoMatches, null));
        Bind.Visible(Add(noMatches), vm, nameof(vm.HasNoMatches), s => s.HasNoMatches);

        _rules = Add(Group(null));
        Bind.Collection(vm.Rules, QueueRebuild);
        Bind.Bind(vm, nameof(vm.IsShowingSubset), s => s.IsShowingSubset, _ => UpdateFooter());
        Bind.Bind(vm, nameof(vm.ShowingText), s => s.ShowingText, _ => UpdateFooter());

        var showAll = Group(null, ActionCard(Translator.RemoteContentRules_ShowAll, vm.ShowAllCommand));
        Bind.Visible(Add(showAll), vm, nameof(vm.IsShowingSubset), s => s.IsShowingSubset);
    }

    private void UpdateFooter()
    {
        if (_rules is null) return;
        _rules.Footer = VM.IsShowingSubset ? VM.ShowingText : Translator.RemoteContentRules_Footer;
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Page.Post(() =>
        {
            _rebuildQueued = false;
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        if (_rules is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _rules.Clear();

        foreach (var rule in VM.Rules.ToList())
        {
            _rules.Add(RuleCard(rows, rule));

            // A policy change updates the row in place without touching the collection.
            PropertyChangedEventHandler changed = (_, _) => QueueRebuild();
            rule.PropertyChanged += changed;
            _rowScope.Own(new ActionDisposable(() => rule.PropertyChanged -= changed));
        }

        UpdateFooter();
        _rules.Visibility = _rules.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private WinoSettingsCard RuleCard(SettingsBinder rows, RemoteContentRuleViewModel rule)
    {
        var entries = new List<SettingsMenuEntry>();
        foreach (var option in rule.PolicyOptions)
        {
            if (option.Policy is not { } policy) continue;
            entries.Add(new(option.Title, () => AccountSettingsParts.Run(rule.SetPolicyCommand.ExecuteAsync(policy), ReportError),
                IsChecked: policy == rule.Policy));
        }
        entries.Add(new(Translator.Buttons_Delete, () => AccountSettingsParts.Run(VM.RemoveRuleCommand.ExecuteAsync(rule), ReportError), IsDestructive: true));

        var more = AccountSettingsParts.MoreButton(Ctx, rows, AccountSettingsParts.ActionLabel(Translator.More, rule.DisplayName), entries);

        var details = new List<string>();
        if (rule.HasDetailText) details.Add(rule.DetailText);
        details.Add($"{rule.PolicyText} · {rule.KindText}");
        details.Add(rule.AddedText);

        var card = new WinoSettingsCard(Ctx, rule.DisplayName, string.Join("\n", details), WinoIconGlyph.None, more)
        {
            LeadingView = AccountSettingsParts.Glyph(Ctx, rule.IsDomain ? WinoIconGlyph.Globe : WinoIconGlyph.Person, Resource.Dimension.wino_glyph_m)
        };
        card.ContentDescription = $"{rule.DisplayName}, {rule.PolicyText}, {rule.KindText}";
        return card;
    }
}
