using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Sender rules (Windows RemoteContentRulesPage, Mac RemoteContentRulesPageViewController): add a rule for an address
/// or a domain with the policy legend as the footer, the spoofing note, the count, policy filter and search, one row
/// per rule with its policy menu and Delete, the empty state and the footer.
/// </summary>
public sealed class RemoteContentRulesPageViewController(RemoteContentRulesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<RemoteContentRulesPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _rules = new();
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // New rule: the pattern field, then the policy menu and Add.
        var pattern = Bind.TextField(vm, nameof(vm.NewPattern), s => s.NewPattern, (s, v) => s.NewPattern = v, Translator.RemoteContentRules_PatternPlaceholder, 0);
        pattern.AutocapitalizationType = UITextAutocapitalizationType.None;
        pattern.AutocorrectionType = UITextAutocorrectionType.No;
        pattern.SpellCheckingType = UITextSpellCheckingType.No;
        pattern.KeyboardType = UIKeyboardType.EmailAddress;
        pattern.ReturnKeyType = UIReturnKeyType.Done;
        WinoAccessibility.Label(pattern, Translator.RemoteContentRules_PatternPlaceholder);
        Bind.OnActivated(pattern, () =>
        {
            if (vm.AddRuleCommand.CanExecute(null)) vm.AddRuleCommand.Execute(null);
        }, UIControlEvent.EditingDidEndOnExit);
        var policy = Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyOptions, option => option.Title,
            nameof(vm.NewPolicyOption), s => s.NewPolicyOption, (s, v) => s.NewPolicyOption = v);
        WinoAccessibility.Label(policy, Translator.RemoteContentRules_ColumnPolicy);
        var add = Bind.Button(Translator.RemoteContentRules_AddRule, vm.AddRuleCommand, primary: true);
        var actions = WinoLayout.HStack(8, policy, WinoLayout.Spacer(), add);
        actions.Alignment = UIStackViewAlignment.Center;
        var entry = WinoLayout.VStack(10, pattern, actions);
        entry.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Size(pattern, height: 36);

        var newRule = Group(null, StackedCard(Translator.RemoteContentRules_AddRule, null, WinoIconGlyph.Add, entry));
        newRule.Footer = string.Join("\n", vm.PolicyOptions.Select(option => $"{option.Title}: {option.Legend}"));
        Add(newRule);

        var spoofing = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.RemoteContentRules_SpoofingInfo);
        spoofing.IsClosable = false;
        Add(spoofing);

        // Count, policy filter and search.
        var count = Bind.Label(vm, nameof(vm.CountText), s => s.CountText, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var filter = Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyFilterOptions, option => option.Title,
            nameof(vm.SelectedPolicyFilterOption), s => s.SelectedPolicyFilterOption, (s, v) => s.SelectedPolicyFilterOption = v);
        WinoAccessibility.Label(filter, Translator.RemoteContentRules_AllPolicies);
        var countRow = WinoLayout.HStack(8, count, WinoLayout.Spacer(), filter);
        countRow.Alignment = UIStackViewAlignment.Center;
        var search = Bind.TextField(vm, nameof(vm.Filter), s => s.Filter, (s, v) => s.Filter = v, Translator.RemoteContentRules_SearchPlaceholder, 0);
        search.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        search.AutocapitalizationType = UITextAutocapitalizationType.None;
        search.AutocorrectionType = UITextAutocorrectionType.No;
        search.ReturnKeyType = UIReturnKeyType.Search;
        search.LeftView = new UIImageView(WinoIcons.Image(WinoIconGlyph.Find, 15, WinoStyle.SecondaryText)) { ContentMode = UIViewContentMode.Center, Frame = new CoreGraphics.CGRect(0, 0, 30, 20) };
        search.LeftViewMode = UITextFieldViewMode.Always;
        WinoLayout.Size(search, height: 36);
        WinoAccessibility.Label(search, Translator.RemoteContentRules_SearchPlaceholder);
        var toolbar = WinoLayout.VStack(8, countRow, search);
        toolbar.Alignment = UIStackViewAlignment.Fill;
        Bind.Visible(Add(toolbar), vm, nameof(vm.HasRules), s => s.HasRules);

        Add(_rules);
        Bind.Collection(vm.Rules, Rebuild);

        var noMatches = Caption(Translator.RemoteContentRules_NoMatches);
        Bind.Visible(Add(noMatches), vm, nameof(vm.HasNoMatches), s => s.HasNoMatches);

        var empty = Group(null, Card(Translator.RemoteContentRules_EmptyTitle, Translator.RemoteContentRules_EmptyDescription, WinoIconGlyph.Shield));
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmpty), s => s.IsEmpty);

        // Footer: Showing x of y with Show all while filtered, then the explanation.
        var showing = Bind.Label(vm, nameof(vm.ShowingText), s => s.ShowingText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var showAll = new SettingsPillButton(Translator.RemoteContentRules_ShowAll, washed: false);
        Bind.OnActivated(showAll, () => vm.ShowAllCommand.Execute(null));
        var subset = WinoLayout.HStack(8, showing, showAll);
        subset.Alignment = UIStackViewAlignment.Center;
        Bind.Visible(Add(subset), vm, nameof(vm.IsShowingSubset), s => s.IsShowingSubset);
        AddIntro(Translator.RemoteContentRules_Footer);
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _rules.Clear();
        foreach (var rule in ViewModel.Rules.ToList()) _rules.Add(RuleRow(rule, rows));
        _rules.Hidden = _rules.RowCount == 0;
        ConvertRows(_rules);
    }

    /// <summary>A rule row: avatar or globe, the sender with its type, detail and date, the policy menu and Delete.</summary>
    private WinoSettingsCard RuleRow(RemoteContentRuleViewModel rule, SettingsBinder rows)
    {
        var card = new WinoSettingsCard(rule.DisplayName, RuleDescription(rule));
        rows.Bind(rule, nameof(rule.DisplayName), r => r.DisplayName, name => card.Header = name);
        rows.Bind(rule, nameof(rule.DetailText), r => RuleDescription(r), text => card.Description = text);

        if (rule.IsDomain)
        {
            var globe = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 16, TranslatesAutoresizingMaskIntoConstraints = false };
            var icon = new UIImageView { Image = WinoIcons.Image(WinoIconGlyph.Globe, 16, WinoStyle.SecondaryText), TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(globe, 32, 32);
            globe.AddSubview(icon);
            icon.CenterXAnchor.ConstraintEqualTo(globe.CenterXAnchor).Active = true;
            icon.CenterYAnchor.ConstraintEqualTo(globe.CenterYAnchor).Active = true;
            card.LeadingView = globe;
        }
        else
        {
            var avatar = new WinoContactPicture(32);
            avatar.SetIdentity(rule.DisplayName, rule.Pattern);
            card.LeadingView = avatar;
        }

        var policy = rows.PopUp<RemoteContentRuleViewModel, RemoteContentPolicyOption>(rule, r => r.PolicyOptions, option => option.Title,
            nameof(rule.SelectedPolicyOption), r => r.SelectedPolicyOption, (r, v) => r.SelectedPolicyOption = v);
        WinoAccessibility.Label(policy, rule.PolicyAutomationName);
        var dot = new WinoSurfaceView { CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(dot, 8, 8);
        rows.Bind(rule, nameof(rule.SelectedPolicyOption), r => r.SelectedPolicyOption?.Policy ?? r.Policy, value => dot.Fill = PolicyColor(value));
        var delete = new SettingsGlyphButton(WinoIconGlyph.Delete, rule.DeleteAutomationName);
        rows.OnActivated(delete, () => rule.RemoveCommand.Execute(null));
        var controls = WinoLayout.HStack(4, dot, policy, delete);
        controls.Alignment = UIStackViewAlignment.Center;
        card.Content = controls;
        card.ContentPlacement = WinoSettingsContentPlacement.Below;
        return card;
    }

    private static string RuleDescription(RemoteContentRuleViewModel rule)
        => rule.HasDetailText ? $"{rule.KindText} · {rule.DetailText}\n{rule.AddedText}" : $"{rule.KindText}\n{rule.AddedText}";

    private static UIColor PolicyColor(RemoteContentSenderPolicy policy) => policy switch
    {
        RemoteContentSenderPolicy.LoadEverything => WinoStyle.Success,
        RemoteContentSenderPolicy.ImagesOnly => WinoStyle.Caution,
        _ => WinoStyle.Critical
    };
}
