using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings.WinoAccountSettings;

/// <summary>
/// Wino add-ons: the Wino Intelligence status rows, Unlimited Accounts (usage meter, purchase or Unlocked) and the
/// Store redeem card, which only exists where the platform sells through a store (the App Store here).
/// Purchases go through PurchaseAddOnCommand, which asks for the channel where several exist.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private IEnumerable<UIView> AddOnCards()
    {
        yield return IntelligenceGroup();
        yield return UnlimitedAccountsCard();
        if (_capabilities.MicrosoftStore || _capabilities.AppleAppStore) yield return StoreRedeemCard();
    }

    /// <summary>
    /// Wino Intelligence at a glance (Windows WinoAccountIntelligenceCard): active or not and the renewal date, then
    /// "See what's included" (the Wino Intelligence page, where usage and settings live) and Get or Renew as rows.
    /// </summary>
    private UIView IntelligenceGroup()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var active = SuccessPill(Translator.WinoAccount_Management_ActiveShort);
        active.AccessibilityIdentifier = "WinoAccountIntelligenceActiveBadge";
        Bind.Visible(active, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        var ended = NeutralPill(Translator.WinoIntelligence_Status_Ended);
        ended.AccessibilityIdentifier = "WinoAccountIntelligenceEndedBadge";
        Bind.Visible(ended, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);
        var status = Card(Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.None,
            WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), active, ended));
        status.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        status.AccessibilityIdentifier = "WinoAccountIntelligenceCard";
        Bind.Bind(vm, nameof(vm.AiPackStatusText), s => s.AiPackStatusText, text => status.Description = text);

        var included = CommandCard(Translator.WinoAccount_Management_SeeWhatsIncluded, null, WinoIconGlyph.None, vm.OpenIntelligencePageCommand);
        included.AccessibilityIdentifier = "WinoAccountIntelligenceSeeWhatsIncludedButton";
        Bind.Visible(included, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
        // The store sheet states the price in the customer's currency; the app does not repeat it.
        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        get.AccessibilityIdentifier = "WinoAccountAddOnPurchaseButton";
        Bind.Visible(get, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
        var renew = Bind.Button(Translator.WinoAccount_Management_Renew, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        renew.AccessibilityIdentifier = "WinoAccountAddOnRenewButton";
        Bind.Visible(renew, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);

        var group = new WinoSettingsGroup();
        group.AddRange(status, included, ActionRow(get), ActionRow(renew));

        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoStyle.Caption, WinoStyle.Caution, 0);
        var errorHost = WinoLayout.Insets(WinoLayout.VStack(0, error), 4, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        Bind.Visible(errorHost, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
        group.Add(errorHost);

        if (_capabilities.AppleAppStore)
        {
            // Guideline 3.1.2: the price, length, renewal and the Terms of Use and Privacy Policy links sit next to the purchase.
            var price = Bind.Label(vm, nameof(vm.AppStoreAiPackPriceText), s => s.AppStoreAiPackPriceText, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
            var terms = WinoLayout.Insets(WinoLayout.VStack(4, price, AppStoreSubscriptionTerms.Create()), 6, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
            terms.Alignment = UIStackViewAlignment.Fill;
            Bind.Visible(terms, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);
            group.Add(terms);
        }
        return group;
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
