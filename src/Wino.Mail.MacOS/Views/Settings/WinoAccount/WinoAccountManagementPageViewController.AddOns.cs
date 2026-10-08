using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Wino add-ons: Wino Intelligence (the promotion while not purchased, the billing and allowance
/// expander once it is), Unlimited Accounts (usage meter, purchase or Unlocked) and the Microsoft
/// Store redeem card, which only exists where the platform sells through the Store.
/// Purchases go through PurchaseAddOnCommand, which asks for the channel where several exist.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private const string IntelligenceLearnMoreUrl = "https://www.winomail.app/intelligence";

    private IEnumerable<NSView> AddOnCards()
    {
        yield return IntelligencePurchaseCard();
        yield return IntelligenceExpander();
        yield return UnlimitedAccountsCard();
        if (_capabilities.MicrosoftStore) yield return StoreRedeemCard();
    }

    /// <summary>Wino Intelligence, not yet purchased: one card that says what the subscription does.</summary>
    private NSView IntelligencePurchaseCard()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var mark = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        var name = WinoStyle.Label(Translator.WinoAddOn_AI_PACK_Name, WinoStyle.Body, WinoStyle.PrimaryText);
        var subtitle = Bind.Label(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(1, name, subtitle);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        subtitle.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        var status = WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), NeutralPill(Translator.WinoAccount_Management_NotActiveShort));
        var header = WinoLayout.HStack(16, mark, text, status);

        var note = WinoStyle.Label(Translator.WinoAccount_Management_AiPackLocalNote, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        var stack = WinoLayout.VStack(12, header, IntelligencePromo(addOn), note);
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in stack.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;

        var card = Surface(stack, 16, 4);
        card.AccessibilityIdentifier = "WinoAccountIntelligencePurchaseCard";
        Bind.Visible(card, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);
        return card;
    }

    /// <summary>The promotion on the brand surface: illustration, pitch, feature chips, then Get / Learn more / price note / error.</summary>
    private NSView IntelligencePromo(WinoAddOnItemViewModel addOn)
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView();
        var title = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPromoTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var description = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPromoDescription, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        description.WidthAnchor.ConstraintLessThanOrEqualTo(520).Active = true;
        var pitch = WinoLayout.VStack(4, title, description);
        pitch.Alignment = NSLayoutAttribute.Leading;
        var chips = IntelligenceViews.Flow(
        [
            WinoIntelligenceBrand.Chip(WinoIconGlyph.DailyBriefing, Translator.WinoAccount_Management_AiPackFeatureBriefing),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Note, Translator.WinoAccount_Management_AiPackFeatureSummarize),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Translate, Translator.WinoAccount_Management_AiPackFeatureTranslate),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Edit, Translator.WinoAccount_Management_AiPackFeatureRewrite),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Tag, Translator.WinoAccount_Management_AiPackFeatureLabels),
        ], 8, 400);
        var column = WinoLayout.VStack(12, pitch, chips);
        column.Alignment = NSLayoutAttribute.Leading;
        column.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        // Stripe Checkout states the price in the customer's currency; the app does not repeat it.
        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        get.SetValueForKey(new NSString("WinoAccountAddOnPurchaseButton"), new NSString("accessibilityIdentifier"));
        var learn = new NSButton { Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        learn.SetButtonType(NSButtonType.MomentaryChange);
        learn.AttributedTitle = new NSAttributedString(Translator.Buttons_LearnMore, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = NSFont.SystemFontOfSize(12) });
        WinoAccessibility.Label(learn, Translator.Buttons_LearnMore);
        Bind.OnActivated(learn, () => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(IntelligenceLearnMoreUrl)));
        var price = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPriceAtCheckout, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        price.Alignment = NSTextAlignment.Right;
        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoStyle.Caption, WinoStyle.Caution, 0);
        error.Alignment = NSTextAlignment.Right;
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
        var side = WinoLayout.VStack(8, get, learn, price, error);
        side.Alignment = NSLayoutAttribute.CenterX;
        WinoLayout.Size(side, 180);
        foreach (var view in new NSView[] { get, price, error }) view.WidthAnchor.ConstraintEqualTo(side.WidthAnchor).Active = true;

        var row = WinoLayout.HStack(20, art, column, side);
        row.Alignment = NSLayoutAttribute.CenterY;
        var surface = new WinoSurfaceView
        {
            // Windows WinoIntelligenceBrandSurfaceBrush: a faint wash of the brand colour.
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x3B84E8, 0.07), WinoStyle.Hex(0x7FBAFF, 0.08)),
            CornerRadius = 4
        };
        WinoLayout.Fill(row, surface, 20);
        return surface;
    }

    /// <summary>Wino Intelligence, purchased: billing period, monthly allowance and usage, then where mailboxes and consent are managed.</summary>
    private NSView IntelligenceExpander()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var status = WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), SuccessPill(Translator.WinoAccount_Management_ActiveShort));
        var expander = new WinoSettingsExpander(Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.None, status, isExpanded: true);
        expander.HeaderCard.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        expander.AccessibilityIdentifier = "WinoAccountIntelligenceExpander";
        Bind.Bind(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, text => expander.HeaderCard.Description = text);

        expander.Add(UsageSummary(), 14, WinoSettingsStyle.NestedIndent, 16, WinoSettingsStyle.CardPadding);

        var mailboxes = Card(Translator.WinoAccount_Management_BriefingMailboxesTitle, null, WinoIconGlyph.Mail,
            Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand));
        Bind.Bind(vm, nameof(vm.IntelligenceMailboxesSummary), s => s.IntelligenceMailboxesSummary, text => mailboxes.Description = text);
        expander.Add(mailboxes);
        expander.Add(Card(Translator.WinoAccount_ConsentNavigationTitle, Translator.WinoAccount_ConsentNavigationDescription, WinoIconGlyph.LockClosed,
            Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand)));

        Bind.Visible(expander, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        return expander;
    }

    /// <summary>The Windows usage card: two facts side by side, the requests meter with its summary, renewal and reset lines, and the limits note.</summary>
    private NSView UsageSummary()
    {
        var vm = ViewModel;
        NSStackView Fact(string caption, string property, Func<WinoAccountManagementPageViewModel, string> read)
        {
            var label = WinoStyle.Label(caption, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
            var value = Bind.Label(vm, property, read, WinoStyle.Body, WinoStyle.PrimaryText, 0);
            value.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
            var fact = WinoLayout.VStack(2, label, value);
            fact.Alignment = NSLayoutAttribute.Leading;
            value.WidthAnchor.ConstraintEqualTo(fact.WidthAnchor).Active = true;
            return fact;
        }
        // The amount is Stripe's to state; the period is the fact Wino can report.
        var facts = WinoLayout.HStack(24,
            Fact(Translator.WinoAccount_Management_AiPackBillingPeriodLabel, nameof(vm.AiPackBillingPeriodText), s => s.AiPackBillingPeriodText),
            Fact(Translator.WinoAccount_Management_IncludedEachMonth, nameof(vm.IntelligenceIncludedText), s => s.IntelligenceIncludedText));
        facts.Distribution = NSStackViewDistribution.FillEqually;
        facts.Alignment = NSLayoutAttribute.Top;

        var requests = WinoStyle.Label(Translator.WinoAccount_Management_AiPackRequestsUsed, WinoStyle.Body, WinoStyle.PrimaryText);
        var summary = Bind.Label(vm, nameof(vm.IntelligenceUsageSummary), s => s.IntelligenceUsageSummary, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var meter = new WinoBarView();
        WinoAccessibility.Label(meter, Translator.WinoAccount_Management_AiPackRequestsUsed);
        meter.AccessibilityIdentifier = "WinoAccountIntelligenceUsageProgressBar";
        Bind.Bind(vm, nameof(vm.IntelligenceUsagePercentage), s => s.IntelligenceUsagePercentage, value => meter.Value = value);
        Bind.Visible(meter, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        var renewal = Bind.Label(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        renewal.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        var reset = Bind.Label(vm, nameof(vm.IntelligenceResetText), s => s.IntelligenceResetText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var usage = WinoLayout.VStack(7,
            WinoLayout.HStack(8, requests, WinoLayout.Spacer(), summary),
            meter,
            WinoLayout.HStack(8, renewal, WinoLayout.Spacer(), reset));
        usage.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in usage.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(usage.WidthAnchor).Active = true;

        var note = WinoStyle.Label(Translator.WinoAccount_Management_UsageOtherLimitsNote, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        var stack = WinoLayout.VStack(18, facts, usage, note);
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in stack.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        stack.AccessibilityIdentifier = "WinoAccountIntelligenceUsageCard";
        return stack;
    }

    /// <summary>
    /// Unlimited Accounts, kept compact: the meter while the free limit applies (a full meter means
    /// no headroom, so it goes once the limit is lifted), the purchase button or the Unlocked badge.
    /// </summary>
    private NSView UnlimitedAccountsCard()
    {
        var vm = ViewModel;
        var addOn = vm.UnlimitedAccountsAddOn;

        var usageText = Bind.Label(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var meter = new WinoBarView();
        meter.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsUsageProgressBar";
        Bind.Bind(vm, nameof(vm.AccountUsagePercentage), s => s.AccountUsagePercentage, value => meter.Value = value);
        Bind.Bind(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, text => WinoAccessibility.Label(meter, text));
        var usage = WinoLayout.VStack(6, usageText, meter);
        usage.Alignment = NSLayoutAttribute.Leading;
        WinoLayout.Size(usage, 150);
        meter.WidthAnchor.ConstraintEqualTo(usage.WidthAnchor).Active = true;
        Bind.Visible(usage, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);

        var purchase = Bind.Button(Translator.Buttons_Purchase, vm.PurchaseAddOnCommand, () => vm.UnlimitedAccountsAddOn, primary: true);
        purchase.SetValueForKey(new NSString("WinoAccountUnlimitedAccountsPurchaseButton"), new NSString("accessibilityIdentifier"));
        Bind.Visible(purchase, addOn, nameof(addOn.ShowPurchaseState), a => a.ShowPurchaseState);
        var unlocked = SuccessPill(Translator.WinoAccount_Management_Unlocked);
        unlocked.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsPurchasedBadge";
        Bind.Visible(unlocked, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);

        var content = WinoLayout.HStack(16, usage, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), purchase, unlocked);
        var card = Card(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, null, WinoIconGlyph.None, content);
        // Windows UnlimitedAccountsMarkBrush (amber); the lighter stop reads better on dark cards.
        card.LeadingView = new WinoIconView(WinoIconGlyph.People, 20, WinoStyle.Dynamic(WinoStyle.Hex(0xD97706), WinoStyle.Hex(0xFBBF24)));
        card.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsCard";
        Bind.Bind(vm, nameof(vm.UnlimitedAccountsSubtitleText), s => s.UnlimitedAccountsSubtitleText, text => card.Description = text);
        return card;
    }

    /// <summary>Redeeming a Microsoft Store purchase is the user's choice; the card appears only while a redeem is possible.</summary>
    private NSView StoreRedeemCard()
    {
        var vm = ViewModel;
        var redeem = Bind.Button(Translator.WinoAccount_StoreRedeem_Button, vm.RedeemStorePurchaseCommand, primary: true);
        var content = WinoLayout.HStack(16, BusySpinner(vm, nameof(vm.IsStoreRedeemInProgress), s => s.IsStoreRedeemInProgress), redeem);
        var card = Card(Translator.WinoAccount_StoreRedeem_Title, Translator.WinoAccount_StoreRedeem_Description, WinoIconGlyph.MicrosoftStore, content);
        card.AccessibilityIdentifier = "WinoAccountRedeemStorePurchaseCard";
        return Bind.Visible(card, vm, nameof(vm.ShowStoreRedeemCard), s => s.ShowStoreRedeemCard);
    }
}
