using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Wino add-ons: Wino Intelligence (the promotion while not purchased, the billing and allowance
/// expander once it is), Unlimited Accounts (usage meter, purchase or Unlocked) and the Store redeem
/// card, which only exists where the platform sells through a store (the App Store here).
/// Purchases go through PurchaseAddOnCommand, which asks for the channel where several exist.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private const string IntelligenceLearnMoreUrl = "https://www.winomail.app/intelligence";

    private IEnumerable<UIView> AddOnCards()
    {
        yield return IntelligencePurchaseCard();
        yield return IntelligenceExpander();
        yield return UnlimitedAccountsCard();
        if (_capabilities.MicrosoftStore || _capabilities.AppleAppStore) yield return StoreRedeemCard();
    }

    /// <summary>Wino Intelligence, not yet purchased: one card that says what the subscription does.</summary>
    private UIView IntelligencePurchaseCard()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var mark = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        var name = WinoStyle.Label(Translator.WinoAddOn_AI_PACK_Name, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var subtitle = Bind.Label(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(1, name, subtitle);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var status = WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), NeutralPill(Translator.WinoAccount_Management_NotActiveShort));
        WinoLayout.Priorities(status, UILayoutConstraintAxis.Horizontal, 750, 1000);
        var header = WinoLayout.HStack(16, mark, text, status);

        var note = WinoStyle.Label(Translator.WinoAccount_Management_AiPackLocalNote, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var stack = WinoLayout.VStack(12, header, IntelligencePromo(addOn), note);
        stack.Alignment = UIStackViewAlignment.Fill;

        var card = Surface(stack, 16, 4);
        card.AccessibilityIdentifier = "WinoAccountIntelligencePurchaseCard";
        Bind.Visible(card, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);
        return card;
    }

    /// <summary>
    /// The promotion on the brand surface: illustration and pitch, feature chips, then Get / Learn more / price note /
    /// error. The Mac row (art, copy, button column) stacks vertically on phone width.
    /// </summary>
    private UIView IntelligencePromo(WinoAddOnItemViewModel addOn)
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView(64);
        var title = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPromoTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var description = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPromoDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var pitch = WinoLayout.VStack(4, title, description);
        pitch.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(pitch, UILayoutConstraintAxis.Horizontal, 1, 250);
        var top = WinoLayout.HStack(16, art, pitch);
        top.Alignment = UIStackViewAlignment.Top;
        var chips = IntelligenceViews.Flow(
        [
            WinoIntelligenceBrand.Chip(WinoIconGlyph.DailyBriefing, Translator.WinoAccount_Management_AiPackFeatureBriefing),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Note, Translator.WinoAccount_Management_AiPackFeatureSummarize),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Translate, Translator.WinoAccount_Management_AiPackFeatureTranslate),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Edit, Translator.WinoAccount_Management_AiPackFeatureRewrite),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Tag, Translator.WinoAccount_Management_AiPackFeatureLabels),
        ], 8);

        // The store sheet states the price in the customer's currency; the app does not repeat it.
        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        get.AccessibilityIdentifier = "WinoAccountAddOnPurchaseButton";
        get.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var learn = IntelligenceViews.LinkButton(Translator.Buttons_LearnMore);
        Bind.OnActivated(learn, () => IntelligenceViews.OpenUrl(IntelligenceLearnMoreUrl));
        var learnRow = WinoLayout.HStack(0, WinoLayout.Spacer(), learn, WinoLayout.Spacer());
        learnRow.Distribution = UIStackViewDistribution.EqualCentering;
        var price = WinoStyle.Label(Translator.WinoAccount_Management_AiPackPriceAtCheckout, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        price.TextAlignment = UITextAlignment.Center;
        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoStyle.Caption, WinoStyle.Caution, 0);
        error.TextAlignment = UITextAlignment.Center;
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);

        var column = WinoLayout.VStack(12, top, chips, get, learnRow, price, error);
        column.Alignment = UIStackViewAlignment.Fill;
        column.SetCustomSpacing(16, chips);
        column.SetCustomSpacing(4, get);
        column.SetCustomSpacing(0, learnRow);
        var surface = new WinoSurfaceView
        {
            // Windows WinoIntelligenceBrandSurfaceBrush: a faint wash of the brand colour.
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x3B84E8, 0.07), WinoStyle.Hex(0x7FBAFF, 0.08)),
            CornerRadius = 4,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(column, surface, 16);
        return surface;
    }

    /// <summary>Wino Intelligence, purchased: billing period, monthly allowance and usage, then where mailboxes and consent are managed.</summary>
    private UIView IntelligenceExpander()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var status = WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), SuccessPill(Translator.WinoAccount_Management_ActiveShort));
        var expander = new WinoSettingsExpander(Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.None, status, isExpanded: true);
        expander.HeaderCard.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        expander.AccessibilityIdentifier = "WinoAccountIntelligenceExpander";
        Bind.Bind(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, text => expander.HeaderCard.Description = text);

        expander.Add(UsageSummary(), 14, WinoSettingsStyle.CardPadding, 16, WinoSettingsStyle.CardPadding);

        var mailboxes = StackedCard(Translator.WinoAccount_Management_BriefingMailboxesTitle, null, WinoIconGlyph.Mail,
            Row(Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand), WinoLayout.Spacer()));
        Bind.Bind(vm, nameof(vm.IntelligenceMailboxesSummary), s => s.IntelligenceMailboxesSummary, text => mailboxes.Description = text);
        expander.Add(mailboxes);
        expander.Add(StackedCard(Translator.WinoAccount_ConsentNavigationTitle, Translator.WinoAccount_ConsentNavigationDescription, WinoIconGlyph.LockClosed,
            Row(Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand), WinoLayout.Spacer())));

        Bind.Visible(expander, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        return expander;
    }

    /// <summary>The Windows usage card: two facts side by side, the requests meter with its summary, renewal and reset lines, and the limits note.</summary>
    private UIView UsageSummary()
    {
        var vm = ViewModel;
        UIStackView Fact(string caption, string property, Func<WinoAccountManagementPageViewModel, string> read)
        {
            var label = WinoStyle.Label(caption, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
            var value = Bind.Label(vm, property, read, WinoStyle.Body, WinoStyle.PrimaryText, 0);
            var fact = WinoLayout.VStack(2, label, value);
            fact.Alignment = UIStackViewAlignment.Fill;
            return fact;
        }
        // The amount is the store's to state; the period is the fact Wino can report.
        var facts = WinoLayout.HStack(24,
            Fact(Translator.WinoAccount_Management_AiPackBillingPeriodLabel, nameof(vm.AiPackBillingPeriodText), s => s.AiPackBillingPeriodText),
            Fact(Translator.WinoAccount_Management_IncludedEachMonth, nameof(vm.IntelligenceIncludedText), s => s.IntelligenceIncludedText));
        facts.Distribution = UIStackViewDistribution.FillEqually;
        facts.Alignment = UIStackViewAlignment.Top;

        var requests = WinoStyle.Label(Translator.WinoAccount_Management_AiPackRequestsUsed, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        WinoLayout.Priorities(requests, UILayoutConstraintAxis.Horizontal, 1, 250);
        var summary = Bind.Label(vm, nameof(vm.IntelligenceUsageSummary), s => s.IntelligenceUsageSummary, WinoStyle.Caption, WinoStyle.SecondaryText);
        WinoLayout.Priorities(summary, UILayoutConstraintAxis.Horizontal, 750, 1000);
        var meter = new WinoBarView();
        WinoAccessibility.Label(meter, Translator.WinoAccount_Management_AiPackRequestsUsed);
        meter.AccessibilityIdentifier = "WinoAccountIntelligenceUsageProgressBar";
        Bind.Bind(vm, nameof(vm.IntelligenceUsagePercentage), s => s.IntelligenceUsagePercentage, value => meter.Value = value);
        Bind.Visible(meter, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        // Renewal and reset sit side by side on the Mac; they stack on phone width.
        var renewal = Bind.Label(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var reset = Bind.Label(vm, nameof(vm.IntelligenceResetText), s => s.IntelligenceResetText, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var usage = WinoLayout.VStack(7, WinoLayout.HStack(8, requests, summary), meter, renewal, reset);
        usage.Alignment = UIStackViewAlignment.Fill;
        usage.SetCustomSpacing(2, renewal);

        var note = WinoStyle.Label(Translator.WinoAccount_Management_UsageOtherLimitsNote, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var stack = WinoLayout.VStack(18, facts, usage, note);
        stack.Alignment = UIStackViewAlignment.Fill;
        stack.AccessibilityIdentifier = "WinoAccountIntelligenceUsageCard";
        return stack;
    }

    /// <summary>
    /// Unlimited Accounts, kept compact: the meter while the free limit applies (a full meter means
    /// no headroom, so it goes once the limit is lifted), the purchase button or the Unlocked badge.
    /// The controls sit under the text on phone width.
    /// </summary>
    private UIView UnlimitedAccountsCard()
    {
        var vm = ViewModel;
        var addOn = vm.UnlimitedAccountsAddOn;

        var usageText = Bind.Label(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, WinoStyle.Caption, WinoStyle.SecondaryText);
        var meter = new WinoBarView();
        meter.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsUsageProgressBar";
        Bind.Bind(vm, nameof(vm.AccountUsagePercentage), s => s.AccountUsagePercentage, value => meter.Value = value);
        Bind.Bind(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, text => WinoAccessibility.Label(meter, text));
        var usage = WinoLayout.VStack(6, usageText, meter);
        usage.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(usage, UILayoutConstraintAxis.Horizontal, 1, 250);
        Bind.Visible(usage, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);

        var purchase = Bind.Button(Translator.Buttons_Purchase, vm.PurchaseAddOnCommand, () => vm.UnlimitedAccountsAddOn, primary: true);
        purchase.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsPurchaseButton";
        WinoLayout.Priorities(purchase, UILayoutConstraintAxis.Horizontal, 750, 1000);
        Bind.Visible(purchase, addOn, nameof(addOn.ShowPurchaseState), a => a.ShowPurchaseState);
        var unlocked = SuccessPill(Translator.WinoAccount_Management_Unlocked);
        unlocked.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsPurchasedBadge";
        Bind.Visible(unlocked, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);

        var content = WinoLayout.HStack(16, usage, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), purchase, unlocked);
        var card = StackedCard(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, null, WinoIconGlyph.None, content);
        // Windows UnlimitedAccountsMarkBrush (amber); the lighter stop reads better on dark cards.
        var people = new WinoIconView(WinoIconGlyph.People, 20, WinoStyle.Dynamic(WinoStyle.Hex(0xD97706), WinoStyle.Hex(0xFBBF24)));
        WinoLayout.Size(people, 20, 20);
        card.LeadingView = people;
        card.AccessibilityIdentifier = "WinoAccountUnlimitedAccountsCard";
        Bind.Bind(vm, nameof(vm.UnlimitedAccountsSubtitleText), s => s.UnlimitedAccountsSubtitleText, text => card.Description = text);
        return card;
    }

    /// <summary>Redeeming a store purchase is the user's choice; the card appears only while a redeem is possible.</summary>
    private UIView StoreRedeemCard()
    {
        var vm = ViewModel;
        var redeem = Bind.Button(Translator.WinoAccount_StoreRedeem_Button, vm.RedeemStorePurchaseCommand, primary: true);
        var content = WinoLayout.HStack(16, redeem, BusySpinner(vm, nameof(vm.IsStoreRedeemInProgress), s => s.IsStoreRedeemInProgress), WinoLayout.Spacer());
        // App Store builds redeem an App Store purchase made without a Wino Account; Windows a Microsoft Store one.
        var card = _capabilities.AppleAppStore
            ? StackedCard(Translator.WinoAccount_AppStoreRedeem_Title, Translator.WinoAccount_AppStoreRedeem_Description, WinoIconGlyph.Apple, content)
            : StackedCard(Translator.WinoAccount_StoreRedeem_Title, Translator.WinoAccount_StoreRedeem_Description, WinoIconGlyph.MicrosoftStore, content);
        card.AccessibilityIdentifier = "WinoAccountRedeemStorePurchaseCard";
        return Bind.Visible(card, vm, nameof(vm.ShowStoreRedeemCard), s => s.ShowStoreRedeemCard);
    }
}
