using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Sender rules (Windows RemoteContentRulesPage): add a rule for an address or a domain, the policy legend, the
/// spoofing note, the count, policy filter and search, one row per rule with its policy pop-up and Delete, the empty
/// state and the footer.
/// </summary>
public sealed class RemoteContentRulesPageViewController(RemoteContentRulesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<RemoteContentRulesPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _rules = new();
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // New rule: pattern, policy and Add, then the legend.
        var pattern = Bind.TextField(vm, nameof(vm.NewPattern), s => s.NewPattern, (s, v) => s.NewPattern = v, Translator.RemoteContentRules_PatternPlaceholder, 0);
        pattern.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(pattern, Translator.RemoteContentRules_PatternPlaceholder);
        Bind.OnActivated(pattern, () =>
        {
            if (vm.AddRuleCommand.CanExecute(null)) vm.AddRuleCommand.Execute(null);
        });
        var policy = Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyOptions, option => option.Title,
            nameof(vm.NewPolicyOption), s => s.NewPolicyOption, (s, v) => s.NewPolicyOption = v, width: 170);
        WinoAccessibility.Label(policy, Translator.RemoteContentRules_ColumnPolicy);
        var add = Bind.Button(Translator.RemoteContentRules_AddRule, vm.AddRuleCommand, primary: true);
        var entry = WinoLayout.HStack(8, pattern, policy, add);
        entry.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        var legend = WinoLayout.VStack(4);
        legend.Alignment = NSLayoutAttribute.Leading;
        foreach (var option in vm.PolicyOptions)
        {
            var text = new NSMutableAttributedString(new NSAttributedString(option.Title, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), ForegroundColor = WinoStyle.PrimaryText }));
            text.Append(new NSAttributedString("  " + option.Legend, new NSStringAttributes { Font = WinoSettingsStyle.CardDescription, ForegroundColor = WinoStyle.SecondaryText }));
            var label = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText, 0);
            label.AttributedStringValue = text;
            var line = WinoLayout.HStack(8, PolicyDot(option.Policy), label);
            line.Alignment = NSLayoutAttribute.CenterY;
            legend.AddArrangedSubview(line);
            line.WidthAnchor.ConstraintEqualTo(legend.WidthAnchor).Active = true;
        }

        var newRule = WinoLayout.VStack(12, entry, legend);
        newRule.Alignment = NSLayoutAttribute.Leading;
        entry.WidthAnchor.ConstraintEqualTo(newRule.WidthAnchor).Active = true;
        legend.WidthAnchor.ConstraintEqualTo(newRule.WidthAnchor).Active = true;
        var newRuleCard = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(newRule, newRuleCard, 16, 16, 16, 16);
        Add(newRuleCard);

        var spoofing = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.RemoteContentRules_SpoofingInfo);
        spoofing.IsClosable = false;
        Add(spoofing);

        // Count, policy filter and search.
        var count = Bind.Label(vm, nameof(vm.CountText), s => s.CountText, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var filter = Bind.PopUp<RemoteContentRulesPageViewModel, RemoteContentPolicyOption>(vm, s => s.PolicyFilterOptions, option => option.Title,
            nameof(vm.SelectedPolicyFilterOption), s => s.SelectedPolicyFilterOption, (s, v) => s.SelectedPolicyFilterOption = v, width: 160);
        WinoAccessibility.Label(filter, Translator.RemoteContentRules_AllPolicies);
        var search = new NSSearchField { PlaceholderString = Translator.RemoteContentRules_SearchPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        search.WidthAnchor.ConstraintEqualTo(220).Active = true;
        WinoAccessibility.Label(search, Translator.RemoteContentRules_SearchPlaceholder);
        Bind.Bind(vm, nameof(vm.Filter), s => s.Filter, value => { if (search.StringValue != (value ?? string.Empty)) search.StringValue = value ?? string.Empty; });
        EventHandler searchChanged = (_, _) => vm.Filter = search.StringValue;
        search.Changed += searchChanged;
        Bindings.Own(new ActionDisposable(() => search.Changed -= searchChanged));
        var toolbar = WinoLayout.HStack(8, count, filter, WinoLayout.Spacer(), search);
        toolbar.EdgeInsets = new NSEdgeInsets(12, 0, 4, 0);
        Bind.Visible(Add(toolbar), vm, nameof(vm.HasRules), s => s.HasRules);

        Add(_rules);
        Bind.Collection(vm.Rules, Rebuild);

        var noMatches = Caption(Translator.RemoteContentRules_NoMatches);
        Bind.Visible(Add(noMatches), vm, nameof(vm.HasNoMatches), s => s.HasNoMatches);

        Bind.Visible(Add(BuildEmptyState()), vm, nameof(vm.IsEmpty), s => s.IsEmpty);

        // Footer: Showing x of y with Show all while filtered, then the explanation.
        var showing = Bind.Label(vm, nameof(vm.ShowingText), s => s.ShowingText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var showAll = new SettingsPillButton(Translator.RemoteContentRules_ShowAll, washed: false);
        Bind.OnActivated(showAll, () => vm.ShowAllCommand.Execute(null));
        var subset = WinoLayout.HStack(8, showing, showAll);
        Bind.Visible(subset, vm, nameof(vm.IsShowingSubset), s => s.IsShowingSubset);
        var footerText = WinoStyle.Label(Translator.RemoteContentRules_Footer, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var footer = WinoLayout.VStack(6, subset, footerText);
        footer.Alignment = NSLayoutAttribute.Leading;
        footerText.WidthAnchor.ConstraintEqualTo(footer.WidthAnchor).Active = true;
        footer.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        Add(footer);
    }

    private NSView BuildEmptyState()
    {
        var icon = new NSImageView { Image = WinoIcons.Image(WinoIconGlyph.Shield, 32, WinoStyle.SecondaryText), TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(icon, 32, 32);
        var title = WinoStyle.Label(Translator.RemoteContentRules_EmptyTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var description = WinoStyle.Label(Translator.RemoteContentRules_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        description.Alignment = NSTextAlignment.Center;
        description.WidthAnchor.ConstraintLessThanOrEqualTo(420).Active = true;
        var stack = WinoLayout.VStack(8, icon, title, description);
        stack.Alignment = NSLayoutAttribute.CenterX;
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        surface.AddSubview(stack);
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(surface.TopAnchor, 32),
            stack.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor, -32),
            stack.CenterXAnchor.ConstraintEqualTo(surface.CenterXAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(surface.LeadingAnchor, 24)
        ]);
        return surface;
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _rules.Clear();
        foreach (var rule in ViewModel.Rules.ToList()) _rules.Add(RuleRow(rule, rows));
        _rules.Hidden = _rules.RowCount == 0;
    }

    /// <summary>A rule row: avatar or globe, sender with its detail and when it was added, the type, the policy and Delete.</summary>
    private static WinoSettingsCard RuleRow(RemoteContentRuleViewModel rule, SettingsBinder rows)
    {
        var card = new WinoSettingsCard(rule.DisplayName, RuleDescription(rule));
        rows.Bind(rule, nameof(rule.DisplayName), r => r.DisplayName, name => card.Header = name);
        rows.Bind(rule, nameof(rule.DetailText), r => RuleDescription(r), text => card.Description = text);

        NSView leading;
        if (rule.IsDomain)
        {
            var globe = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 16, TranslatesAutoresizingMaskIntoConstraints = false };
            var icon = new NSImageView { Image = WinoIcons.Image(WinoIconGlyph.Globe, 16, WinoStyle.SecondaryText), TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(globe, 32, 32);
            globe.AddSubview(icon);
            icon.CenterXAnchor.ConstraintEqualTo(globe.CenterXAnchor).Active = true;
            icon.CenterYAnchor.ConstraintEqualTo(globe.CenterYAnchor).Active = true;
            leading = globe;
        }
        else
        {
            var avatar = new WinoContactPicture(32);
            avatar.SetIdentity(rule.DisplayName, rule.Pattern);
            leading = avatar;
        }
        card.LeadingView = leading;

        var policy = rows.PopUp<RemoteContentRuleViewModel, RemoteContentPolicyOption>(rule, r => r.PolicyOptions, option => option.Title,
            nameof(rule.SelectedPolicyOption), r => r.SelectedPolicyOption, (r, v) => r.SelectedPolicyOption = v, width: 160);
        WinoAccessibility.Label(policy, rule.PolicyAutomationName);
        var delete = new SettingsGlyphButton(WinoIconGlyph.Delete, rule.DeleteAutomationName);
        rows.OnActivated(delete, () => rule.RemoveCommand.Execute(null));
        var dot = PolicyDot(rule.Policy);
        rows.Bind(rule, nameof(rule.SelectedPolicyOption), r => r.SelectedPolicyOption?.Policy ?? r.Policy, value => dot.Fill = PolicyColor(value));
        card.Content = WinoLayout.HStack(10, SettingsRowParts.Tag(rule.KindText), dot, policy, delete);
        return card;
    }

    private static string RuleDescription(RemoteContentRuleViewModel rule)
        => rule.HasDetailText ? $"{rule.DetailText}\n{rule.AddedText}" : rule.AddedText;

    /// <summary>The policy colour; the policy is always named in text next to it.</summary>
    private static WinoSurfaceView PolicyDot(RemoteContentSenderPolicy? policy)
    {
        var dot = new WinoSurfaceView { Fill = PolicyColor(policy), CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(dot, 8, 8);
        dot.Hidden = policy is null;
        return dot;
    }

    private static NSColor PolicyColor(RemoteContentSenderPolicy? policy) => policy switch
    {
        RemoteContentSenderPolicy.LoadEverything => WinoStyle.Success,
        RemoteContentSenderPolicy.ImagesOnly => WinoStyle.Caution,
        _ => WinoStyle.Critical
    };
}
