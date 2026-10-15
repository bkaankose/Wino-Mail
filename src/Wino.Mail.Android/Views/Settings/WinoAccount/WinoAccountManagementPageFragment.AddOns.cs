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
/// Wino add-ons: Wino Intelligence (the promotion while not purchased, the billing and allowance expander once it is),
/// Unlimited Accounts (usage meter, purchase or Unlocked) and the Store redeem card. The purchase buttons, the price
/// note and the redeem card exist only where the platform sells through a store (IPlatformCapabilities.MicrosoftStore
/// or AppleAppStore); Android v1 has neither, so the cards inform and the purchases made elsewhere still show.
/// </summary>
public sealed partial class WinoAccountManagementPageFragment
{
    internal const string IntelligenceLearnMoreUrl = "https://www.winomail.app/intelligence";

    private IEnumerable<View> AddOnCards()
    {
        yield return IntelligencePurchaseCard();
        yield return IntelligenceExpander();
        yield return UnlimitedAccountsCard();
        if (SellsAddOns) yield return StoreRedeemCard();
    }

    /// <summary>Wino Intelligence, not yet purchased: one card that says what the subscription does.</summary>
    private View IntelligencePurchaseCard()
    {
        var vm = VM;
        var addOn = vm.AiPackAddOn;
        var context = Ctx;

        var mark = new BrandIconView(context, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        var name = Text(Translator.WinoAddOn_AI_PACK_Name);
        var subtitle = Bind.Label(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, WinoTextStyle.Description, maximumLines: 0);
        var text = Stack(Resource.Dimension.wino_space_hairline, name, subtitle);
        var status = Row(BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), IntelligenceViews.NeutralPill(context, Translator.WinoAccount_Management_NotActiveShort));
        var header = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_l, text, mark, text, status);

        var note = Text(Translator.WinoAccount_Management_AiPackLocalNote, WinoTextStyle.Caption);
        var stack = Stack(Resource.Dimension.wino_space_m, header, IntelligencePromo(addOn), note);

        var card = Surface(stack, Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_small);
        Bind.Visible(card, addOn, nameof(addOn.IsPurchased), a => a.IsNotPurchased);
        return card;
    }

    /// <summary>
    /// The promotion on the brand surface: illustration and pitch, feature chips, then Get / Learn more / price note /
    /// error. The Mac row (art, copy, button column) stacks vertically on phone width.
    /// </summary>
    private View IntelligencePromo(WinoAddOnItemViewModel addOn)
    {
        var vm = VM;
        var context = Ctx;
        var art = new WinoIntelligencePromotionIllustrationView(context, Resource.Dimension.wino_winoaccount_promo_art_small);
        var title = Text(Translator.WinoAccount_Management_AiPackPromoTitle, WinoTextStyle.BodyStrong);
        var description = Text(Translator.WinoAccount_Management_AiPackPromoDescription, WinoTextStyle.Description);
        var pitch = Stack(Resource.Dimension.wino_space_2xs, title, description);
        var top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        top.AddView(art);
        top.AddView(pitch, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_l) });
        var chips = FeatureChips();

        var column = Stack(Resource.Dimension.wino_space_m, top);
        AddTo(column, chips, Resource.Dimension.wino_space_m);
        if (SellsAddOns)
        {
            // The store sheet states the price in the customer's currency; the app does not repeat it.
            var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
            AddTo(column, get, Resource.Dimension.wino_space_l);
        }
        var learn = IntelligenceViews.LinkButton(context, Translator.Buttons_LearnMore);
        Bind.Views.Click(learn, () => IntelligenceViews.OpenUrl(IntelligenceLearnMoreUrl));
        var learnRow = new FrameLayout(context);
        learnRow.AddView(learn, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));
        AddTo(column, learnRow, SellsAddOns ? Resource.Dimension.wino_space_2xs : Resource.Dimension.wino_space_m);
        if (SellsAddOns)
        {
            var price = Text(Translator.WinoAccount_Management_AiPackPriceAtCheckout, WinoTextStyle.Caption);
            price.Gravity = GravityFlags.CenterHorizontal;
            AddTo(column, price, 0);
        }
        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoTextStyle.Caption, maximumLines: 0);
        error.SetTextColor(WinoColors.Caution(context));
        error.Gravity = GravityFlags.CenterHorizontal;
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
        AddTo(column, error, Resource.Dimension.wino_space_m);

        // Windows WinoIntelligenceBrandSurfaceBrush: a faint wash of the brand colour.
        var wash = WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_wash);
        var surface = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, wash, null, Resource.Dimension.wino_radius_small) };
        WinoTokens.Pad(surface, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l);
        surface.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return surface;
    }

    /// <summary>The five feature chips of the Wino Intelligence promotion (shared with the Intelligence page).</summary>
    internal static View FeatureChips(global::Android.Content.Context context) => IntelligenceViews.Flow(context,
    [
        IntelligenceViews.Chip(context, WinoIconGlyph.DailyBriefing, Translator.WinoAccount_Management_AiPackFeatureBriefing),
        IntelligenceViews.Chip(context, WinoIconGlyph.Note, Translator.WinoAccount_Management_AiPackFeatureSummarize),
        IntelligenceViews.Chip(context, WinoIconGlyph.Translate, Translator.WinoAccount_Management_AiPackFeatureTranslate),
        IntelligenceViews.Chip(context, WinoIconGlyph.Edit, Translator.WinoAccount_Management_AiPackFeatureRewrite),
        IntelligenceViews.Chip(context, WinoIconGlyph.Tag, Translator.WinoAccount_Management_AiPackFeatureLabels),
    ], Resource.Dimension.wino_space_s);

    private View FeatureChips() => FeatureChips(Ctx);

    /// <summary>Wino Intelligence, purchased: billing period, monthly allowance and usage, then where mailboxes and consent are managed.</summary>
    private View IntelligenceExpander()
    {
        var vm = VM;
        var addOn = vm.AiPackAddOn;
        var context = Ctx;

        var status = Row(BusySpinner(addOn, nameof(addOn.IsLoading), a => a.IsLoading), IntelligenceViews.SuccessPill(context, Translator.WinoAccount_Management_ActiveShort));
        var expander = new WinoSettingsExpander(context, Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.None, status, isExpanded: true);
        expander.HeaderCard.LeadingView = new BrandIconView(context, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        Bind.Bind(vm, nameof(vm.AiPackSubtitleText), s => s.AiPackSubtitleText, text => expander.HeaderCard.Description = text);

        expander.Add(UsageSummary(), WinoTokens.Dp(context, Resource.Dimension.wino_space_ml), WinoTokens.Dp(context, Resource.Dimension.wino_space_l), WinoTokens.Dp(context, Resource.Dimension.wino_space_l), WinoTokens.Dp(context, Resource.Dimension.wino_space_l));

        var mailboxes = StackedCard(Translator.WinoAccount_Management_BriefingMailboxesTitle, null, WinoIconGlyph.Mail,
            Row(Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand)));
        Bind.Bind(vm, nameof(vm.IntelligenceMailboxesSummary), s => s.IntelligenceMailboxesSummary, text => mailboxes.Description = text);
        expander.Add(mailboxes);
        expander.Add(StackedCard(Translator.WinoAccount_ConsentNavigationTitle, Translator.WinoAccount_ConsentNavigationDescription, WinoIconGlyph.LockClosed,
            Row(Bind.Button(Translator.Buttons_Manage, vm.OpenIntelligenceManagementCommand))));

        Bind.Visible(expander, addOn, nameof(addOn.IsPurchased), a => a.IsPurchased);
        return expander;
    }

    /// <summary>The Windows usage card: two facts side by side, the requests meter with its summary, renewal and reset lines, and the limits note.</summary>
    private View UsageSummary()
    {
        var vm = VM;
        var context = Ctx;
        LinearLayout Fact(string caption, string property, Func<WinoAccountManagementPageViewModel, string> read)
            => Stack(Resource.Dimension.wino_space_3xs, Text(caption, WinoTextStyle.Caption), Bind.Label(vm, property, read, WinoTextStyle.Body, maximumLines: 0));
        // The amount is the store's to state; the period is the fact Wino can report.
        var facts = new LinearLayout(context) { Orientation = Orientation.Horizontal, WeightSum = 2 };
        facts.AddView(Fact(Translator.WinoAccount_Management_AiPackBillingPeriodLabel, nameof(vm.AiPackBillingPeriodText), s => s.AiPackBillingPeriodText),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        facts.AddView(Fact(Translator.WinoAccount_Management_IncludedEachMonth, nameof(vm.IntelligenceIncludedText), s => s.IntelligenceIncludedText),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_2xl) });

        var requests = Text(Translator.WinoAccount_Management_AiPackRequestsUsed);
        var summary = Bind.Label(vm, nameof(vm.IntelligenceUsageSummary), s => s.IntelligenceUsageSummary, WinoTextStyle.Caption);
        var meter = IntelligenceViews.Meter(context);
        meter.ContentDescription = Translator.WinoAccount_Management_AiPackRequestsUsed;
        Bind.Bind(vm, nameof(vm.IntelligenceUsagePercentage), s => s.IntelligenceUsagePercentage, value => IntelligenceViews.SetMeter(meter, value));
        Bind.Visible(meter, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        // Renewal and reset sit side by side on the Mac; they stack on phone width.
        var renewal = Bind.Label(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, WinoTextStyle.Caption, maximumLines: 0);
        var reset = Bind.Label(vm, nameof(vm.IntelligenceResetText), s => s.IntelligenceResetText, WinoTextStyle.Caption, maximumLines: 0);
        var usage = Stack(Resource.Dimension.wino_space_xs, IntelligenceViews.HStack(context, Resource.Dimension.wino_space_s, requests, requests, summary), meter, renewal);
        AddTo(usage, reset, Resource.Dimension.wino_space_3xs);

        var note = Text(Translator.WinoAccount_Management_UsageOtherLimitsNote, WinoTextStyle.Caption);
        return Stack(Resource.Dimension.wino_space_l, facts, usage, note);
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
