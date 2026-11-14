using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Wino add-ons: Wino Intelligence (status row; the offer, usage and settings are on the Wino Intelligence page),
/// Unlimited Accounts (usage meter, purchase or Unlocked) and the Store redeem card. The purchase buttons, the price
/// note and the redeem card exist only where the platform sells through a store (IPlatformCapabilities.MicrosoftStore
/// or AppleAppStore); Android v1 has neither, so the cards inform and the purchases made elsewhere still show.
/// </summary>
public sealed partial class WinoAccountManagementPageFragment
{
    internal const string IntelligenceLearnMoreUrl = "https://www.winomail.app/intelligence";

    private IEnumerable<View> AddOnCards()
    {
        yield return IntelligenceCard();
        yield return UnlimitedAccountsCard();
        if (SellsAddOns) yield return StoreRedeemCard();
    }

    /// <summary>
    /// Wino Intelligence at a glance (Windows WinoAccountIntelligenceCard): active, not active or ended with the
    /// renewal date. Usage and settings live on the Wino Intelligence page, which "See what's included" opens. Get and
    /// Renew exist only where the platform sells add-ons; without them an ended subscription also links to the page.
    /// </summary>
    private View IntelligenceCard()
    {
        var vm = VM;
        var addOn = vm.AiPackAddOn;
        var context = Ctx;

        var active = IntelligenceViews.SuccessPill(context, Translator.WinoAccount_Management_ActiveShort);
        Bind.Visible(active, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        var notActive = IntelligenceViews.NeutralPill(context, Translator.WinoAccount_Management_NotActiveShort);
        Bind.Visible(notActive, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
        var ended = IntelligenceViews.NeutralPill(context, Translator.WinoIntelligence_Status_Ended);
        Bind.Visible(ended, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);

        var seeIncluded = LinkButton(Translator.WinoAccount_Management_SeeWhatsIncluded, vm.OpenIntelligencePageCommand);
        var controls = new List<View> { BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), active, notActive, ended, seeIncluded };
        if (SellsAddOns)
        {
            Bind.Visible(seeIncluded, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
            // Stripe Checkout states the price in the customer's own currency; the app does not repeat it.
            var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
            Bind.Visible(get, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive);
            var renew = Bind.Button(Translator.WinoAccount_Management_Renew, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
            Bind.Visible(renew, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded);
            controls.Add(get);
            controls.Add(renew);
        }
        else
        {
            Bind.Views.BindAny(vm, s => s.IsAiPackNotActive || s.IsAiPackEnded,
                show => seeIncluded.Visibility = show ? ViewStates.Visible : ViewStates.Gone);
        }

        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoTextStyle.Caption, maximumLines: 0);
        error.SetTextColor(WinoColors.Caution(context));
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);

        var mark = new BrandIconView(context, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        var name = Text(Translator.WinoAddOn_AI_PACK_Name);
        var status = Bind.Label(vm, nameof(vm.AiPackStatusText), s => s.AiPackStatusText, WinoTextStyle.Description, maximumLines: 0);
        var text = Stack(Resource.Dimension.wino_space_hairline, name, status);
        var header = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_l, text, mark, text);
        var stack = Stack(Resource.Dimension.wino_space_m, header, IntelligenceViews.Flow(context, controls, Resource.Dimension.wino_space_s), error);
        return Surface(stack, Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_small);
    }

    /// <summary>
    /// Unlimited Accounts, kept compact: the meter while the free limit applies (a full meter means no headroom, so it
    /// goes once the limit is lifted), the purchase button where the platform sells it, or the Unlocked badge. The
    /// controls sit under the text on phone width.
    /// </summary>
    private View UnlimitedAccountsCard()
    {
        var vm = VM;
        var addOn = vm.UnlimitedAccountsAddOn;
        var context = Ctx;

        var usageText = Bind.Label(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, WinoTextStyle.Caption);
        var meter = IntelligenceViews.Meter(context);
        Bind.Bind(vm, nameof(vm.AccountUsagePercentage), s => s.AccountUsagePercentage, value => IntelligenceViews.SetMeter(meter, value));
        Bind.Bind(vm, nameof(vm.AccountUsageText), s => s.AccountUsageText, text => meter.ContentDescription = text);
        var usage = Stack(Resource.Dimension.wino_space_xs, usageText, meter);
        Bind.Visible(usage, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);

        var unlocked = IntelligenceViews.SuccessPill(context, Translator.WinoAccount_Management_Unlocked);
        Bind.Visible(unlocked, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        var spinner = BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading);

        var content = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        content.SetGravity(GravityFlags.CenterVertical);
        content.AddView(usage, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var trailing = new List<View> { spinner };
        if (SellsAddOns)
        {
            var purchase = Bind.Button(Translator.Buttons_Purchase, vm.PurchaseAddOnCommand, () => vm.UnlimitedAccountsAddOn, primary: true);
            Bind.Visible(purchase, addOn, nameof(addOn.ShowPurchaseState), a => a.ShowPurchaseState);
            trailing.Add(purchase);
        }
        trailing.Add(unlocked);
        foreach (var view in trailing)
            content.AddView(view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_l) });

        var card = StackedCard(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, null, WinoIconGlyph.None, content);
        // Windows UnlimitedAccountsMarkBrush (amber); the lighter stop reads better on dark cards.
        var people = new WinoIconView(context) { Icon = WinoIconGlyph.People, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_l) };
        people.SetTextColor(WinoTokens.Color(context, Resource.Color.wino_winoaccount_unlimited_mark));
        card.LeadingView = people;
        Bind.Bind(vm, nameof(vm.UnlimitedAccountsSubtitleText), s => s.UnlimitedAccountsSubtitleText, text => card.Description = text);
        return card;
    }

    /// <summary>Redeeming a store purchase is the user's choice; the card appears only while a redeem is possible.</summary>
    private View StoreRedeemCard()
    {
        var vm = VM;
        var redeem = Bind.Button(Translator.WinoAccount_StoreRedeem_Button, vm.RedeemStorePurchaseCommand, primary: true);
        var content = Row(redeem, BusySpinner(vm, nameof(vm.IsStoreRedeemInProgress), s => s.IsStoreRedeemInProgress));
        // App Store builds redeem an App Store purchase made without a Wino Account; Windows a Microsoft Store one.
        var card = _capabilities?.AppleAppStore == true
            ? StackedCard(Translator.WinoAccount_AppStoreRedeem_Title, Translator.WinoAccount_AppStoreRedeem_Description, WinoIconGlyph.Apple, content)
            : StackedCard(Translator.WinoAccount_StoreRedeem_Title, Translator.WinoAccount_StoreRedeem_Description, WinoIconGlyph.MicrosoftStore, content);
        return Bind.Visible(card, vm, nameof(vm.ShowStoreRedeemCard), s => s.ShowStoreRedeemCard);
    }

    /// <summary>
    /// Management: Wino Account backups live on the Backup and restore page (Windows WinoAccountBackupRestoreCard),
    /// which Android does not route (IPlatformCapabilities.FileBackup is false), so only account deletion remains: it
    /// opens the website's deletion page (WinoAccountDeleteAccountCard).
    /// </summary>
    private IEnumerable<View> SyncCards()
    {
        var vm = VM;
        var context = Ctx;
        if (_capabilities?.FileBackup == true)
        {
            var card = CommandCard(Translator.SettingsBackupRestore_Title, Translator.WinoAccount_Management_BackupRestoreDescription_Device, WinoIconGlyph.None, vm.OpenBackupRestoreCommand);
            card.LeadingView = LeadingGlyph(WinoIconGlyph.Sync, WinoColors.Accent(context));
            yield return card;
        }

        var delete = CommandCard(Translator.WinoAccount_Management_DeleteAccount_Title, Translator.WinoAccount_Management_DeleteAccount_Description, WinoIconGlyph.None, vm.OpenAccountDeletionCommand);
        delete.LeadingView = LeadingGlyph(WinoIconGlyph.Delete, WinoColors.Critical(context));
        delete.SetActionIcon(WinoIconGlyph.Open);
        yield return delete;
    }

    private WinoIconView LeadingGlyph(WinoIconGlyph glyph, global::Android.Graphics.Color tint)
    {
        var icon = new WinoIconView(Ctx) { Icon = glyph, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_l) };
        icon.SetTextColor(tint);
        return icon;
    }
}
