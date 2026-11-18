using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings.WinoAccount;

/// <summary>
/// Wino add-ons: the Wino Intelligence status row, Unlimited Accounts (usage meter, purchase or Unlocked) and the
/// store redeem card, which only exists where the platform sells through a store.
/// Purchases go through PurchaseAddOnCommand, which asks for the channel where several exist.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private IEnumerable<NSView> AddOnCards()
    {
        yield return IntelligenceCard();
        yield return UnlimitedAccountsCard();
        if (_capabilities.MicrosoftStore || _capabilities.AppleAppStore) yield return StoreRedeemCard();
    }

    /// <summary>
    /// Wino Intelligence at a glance (Windows WinoAccountIntelligenceCard): active or not and the renewal date. Usage and
    /// settings live on the Wino Intelligence page, which "See what's included" opens.
    /// </summary>
    private NSView IntelligenceCard()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;

        var active = SuccessPill(Translator.WinoAccount_Management_ActiveShort);
        active.AccessibilityIdentifier = "WinoAccountIntelligenceActiveBadge";
        Bind.Visible(active, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        var ended = NeutralPill(Translator.WinoIntelligence_Status_Ended);
        ended.AccessibilityIdentifier = "WinoAccountIntelligenceEndedBadge";
        Bind.Visible(ended, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);
        var included = LinkButton(Translator.WinoAccount_Management_SeeWhatsIncluded, vm.OpenIntelligencePageCommand);
        included.SetValueForKey(new NSString("WinoAccountIntelligenceSeeWhatsIncludedButton"), new NSString("accessibilityIdentifier"));
        Bind.Visible(included, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
        // Outside the App Store, Stripe Checkout states the price in the customer's own currency; the app does not repeat it.
        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        get.SetValueForKey(new NSString("WinoAccountAddOnPurchaseButton"), new NSString("accessibilityIdentifier"));
        Bind.Visible(get, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
        var renew = Bind.Button(Translator.WinoAccount_Management_Renew, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        renew.SetValueForKey(new NSString("WinoAccountAddOnRenewButton"), new NSString("accessibilityIdentifier"));
        Bind.Visible(renew, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);
        var controls = WinoLayout.HStack(8, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), active, ended, included, get, renew);

        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoStyle.Caption, WinoStyle.Caution, 0);
        error.Alignment = NSTextAlignment.Right;
        error.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
        var side = WinoLayout.VStack(4, controls, error);
        side.Alignment = NSLayoutAttribute.Trailing;

        var card = Card(Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.None, side);
        card.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        card.AccessibilityIdentifier = "WinoAccountIntelligenceCard";
        Bind.Bind(vm, nameof(vm.AiPackStatusText), s => s.AiPackStatusText, text => card.Description = text);

        if (_capabilities.AppleAppStore)
        {
            // Guideline 3.1.2: the price, length, renewal and the Terms of Use and Privacy Policy links sit next to the purchase.
            var price = Bind.Label(vm, nameof(vm.AppStoreAiPackPriceText), s => s.AppStoreAiPackPriceText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
            var terms = WinoLayout.VStack(6, price, AppStoreSubscriptionTerms.Create());
            terms.Alignment = NSLayoutAttribute.Leading;
            Bind.Visible(terms, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);
            card.BottomContent = terms;
        }
        return card;
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

        var price = Bind.Label(vm, nameof(vm.AppStoreUnlimitedAccountsPriceText), s => s.AppStoreUnlimitedAccountsPriceText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        Bind.Visible(price, addOn, nameof(addOn.ShowPurchaseState), a => a.ShowPurchaseState && _capabilities.AppleAppStore);

        var content = WinoLayout.HStack(16, usage, BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), price, purchase, unlocked);
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
        // App Store builds redeem an App Store purchase made without a Wino Account; Windows a Microsoft Store one.
        var card = _capabilities.AppleAppStore
            ? Card(Translator.WinoAccount_AppStoreRedeem_Title, Translator.WinoAccount_AppStoreRedeem_Description, WinoIconGlyph.Apple, content)
            : Card(Translator.WinoAccount_StoreRedeem_Title, Translator.WinoAccount_StoreRedeem_Description, WinoIconGlyph.MicrosoftStore, content);
        card.AccessibilityIdentifier = "WinoAccountRedeemStorePurchaseCard";
        return Bind.Visible(card, vm, nameof(vm.ShowStoreRedeemCard), s => s.ShowStoreRedeemCard);
    }
}
