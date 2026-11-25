using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>
/// Wino add-ons: Wino Intelligence at a glance and Unlimited Accounts with its usage meter. Buy buttons exist only where
/// the platform can purchase (<c>IsPurchaseAvailable</c>); the store redeem card only where a store exists.
/// </summary>
internal sealed partial class WinoAccountPage
{
    private IEnumerable<Gtk.Widget> AddOnRows()
    {
        yield return IntelligenceRow();
        yield return UnlimitedAccountsRow();
        if (_capabilities.MicrosoftStore || _capabilities.AppleAppStore) yield return StoreRedeemRow();
    }

    private Adw.ActionRow IntelligenceRow()
    {
        var vm = ViewModel;
        var addOn = vm.AiPackAddOn;
        var row = WinoSettingsRows.Action(Translator.WinoAddOn_AI_PACK_Name, null, WinoIconGlyph.WinoIntelligence);
        Settings.Subtitle(row, vm, nameof(vm.AiPackStatusText), static s => s.AiPackStatusText);

        var loading = Settings.Visible(Adw.Spinner.New(), addOn, nameof(addOn.IsLoading), static a => a.IsLoading);
        var active = Settings.Visible(Pill(Translator.WinoAccount_Management_ActiveShort, success: true), addOn, nameof(addOn.IsPurchased), static a => a.IsPurchased);
        var ended = Settings.Visible(Pill(Translator.WinoIntelligence_Status_Ended, success: false), vm, nameof(vm.IsAiPackEnded), static s => s.IsAiPackEnded);
        var included = Settings.Button(Translator.WinoAccount_Management_SeeWhatsIncluded, vm.OpenIntelligencePageCommand);
        included.AddCssClass("flat");
        WinoA11y.Rename(included, Translator.WinoAccount_Management_SeeWhatsIncluded);
        Settings.Visible(included, vm, nameof(vm.IsAiPackNotActive), static s => s.IsAiPackNotActive);
        var purchasable = vm.IsPurchaseAvailable;
        var get = Settings.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, suggested: true);
        WinoA11y.Rename(get, Translator.WinoAccount_Management_AiPackGetButton);
        Settings.Visible(get, vm, nameof(vm.IsAiPackNotActive), s => s.IsAiPackNotActive && purchasable);
        var renew = Settings.Button(Translator.WinoAccount_Management_Renew, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, suggested: true);
        WinoA11y.Rename(renew, Translator.WinoAccount_Management_Renew);
        Settings.Visible(renew, vm, nameof(vm.IsAiPackEnded), s => s.IsAiPackEnded && purchasable);
        var checkout = CheckoutSpinner(addOn);
        foreach (var widget in (Gtk.Widget[])[loading, checkout, active, ended, included, get, renew])
        {
            widget.SetValign(Gtk.Align.Center);
            WinoSettingsRows.Suffix(row, widget);
        }
        return row;
    }

    private Adw.ActionRow UnlimitedAccountsRow()
    {
        var vm = ViewModel;
        var addOn = vm.UnlimitedAccountsAddOn;
        var row = WinoSettingsRows.Action(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, null, WinoIconGlyph.People);
        Settings.Subtitle(row, vm, nameof(vm.UnlimitedAccountsSubtitleText), static s => s.UnlimitedAccountsSubtitleText);

        var usageText = Gtk.Label.New(string.Empty);
        usageText.AddCssClass("caption");
        usageText.AddCssClass("dim-label");
        usageText.SetXalign(0);
        var meter = Gtk.LevelBar.New();
        meter.SetMinValue(0);
        meter.SetMaxValue(100);
        meter.SetSizeRequest(150, -1);
        Settings.Bind(vm, nameof(vm.AccountUsageText), static s => s.AccountUsageText, text =>
        {
            usageText.SetText(text ?? string.Empty);
            WinoA11y.Label(meter, text);
        });
        Settings.Bind(vm, nameof(vm.AccountUsagePercentage), static s => s.AccountUsagePercentage, value => meter.SetValue(Math.Clamp(value, 0, 100)));
        var usage = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        usage.Append(usageText);
        usage.Append(meter);
        Settings.Visible(usage, addOn, nameof(addOn.IsPurchased), static a => a.IsNotPurchased);

        var loading = Settings.Visible(Adw.Spinner.New(), addOn, nameof(addOn.IsLoading), static a => a.IsLoading);
        var purchasable = vm.IsPurchaseAvailable;
        var purchase = Settings.Button(Translator.Buttons_Purchase, vm.PurchaseAddOnCommand, () => vm.UnlimitedAccountsAddOn, suggested: true);
        WinoA11y.Rename(purchase, Translator.Buttons_Purchase);
        Settings.Visible(purchase, addOn, nameof(addOn.ShowPurchaseState), a => a.ShowPurchaseState && purchasable);
        var unlocked = Settings.Visible(Pill(Translator.WinoAccount_Management_Unlocked, success: true), addOn, nameof(addOn.IsPurchased), static a => a.IsPurchased);
        var checkout = CheckoutSpinner(addOn);
        foreach (var widget in (Gtk.Widget[])[usage, loading, checkout, purchase, unlocked])
        {
            widget.SetValign(Gtk.Align.Center);
            WinoSettingsRows.Suffix(row, widget);
        }
        return row;
    }

    /// <summary>Shown beside the add-on whose checkout is being opened; the buy buttons are insensitive meanwhile (command state).</summary>
    private Adw.Spinner CheckoutSpinner(WinoAddOnItemViewModel addOn)
        => Settings.Visible(Adw.Spinner.New(), addOn, nameof(addOn.IsPurchaseInProgress), static a => a.IsPurchaseInProgress);

    private Adw.ActionRow StoreRedeemRow()
    {
        var vm = ViewModel;
        var row = WinoSettingsRows.Action(Translator.WinoAccount_StoreRedeem_Title, Translator.WinoAccount_StoreRedeem_Description, WinoIconGlyph.MicrosoftStore);
        var busy = Settings.Visible(Adw.Spinner.New(), vm, nameof(vm.IsStoreRedeemInProgress), static s => s.IsStoreRedeemInProgress);
        var redeem = Settings.Button(Translator.WinoAccount_StoreRedeem_Button, vm.RedeemStorePurchaseCommand, suggested: true);
        busy.SetValign(Gtk.Align.Center);
        redeem.SetValign(Gtk.Align.Center);
        WinoSettingsRows.Suffix(row, busy);
        WinoSettingsRows.Suffix(row, redeem);
        return Settings.Visible(row, vm, nameof(vm.ShowStoreRedeemCard), static s => s.ShowStoreRedeemCard);
    }
}
