using System.Net;
using System.Text.RegularExpressions;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Signatures of one account (iOS SignatureManagementPageViewController, Windows SignatureManagementPage): the
/// Signature switch, the two default menus (the ViewModel's synthetic "None" first), then the Signatures header with
/// Add signature and one joined card listing each signature with a "Default" pill for the new-message default, a
/// one-line text preview and an actions menu with Edit and Delete. With no signatures a dashed empty card takes the
/// list's place. The signature editor is the ViewModel's dialog. Navigation parameter: the account id.
/// </summary>
public sealed partial class SignatureManagementPageFragment : SettingsPageFragment<SignatureManagementPageViewModel>, ISettingsPageTitleSource
{
    private LinearLayout? _list;
    private TextView? _subtitle;
    private View? _empty;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.SettingsSignature_Title;

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));

        // "Account name · address" under the title.
        _subtitle = Add(Text(string.Empty, WinoTextStyle.BodyStrong, maxLines: 1));
        _subtitle.SetTextColor(WinoColors.SecondaryText(Ctx));
        _subtitle.Visibility = ViewStates.Gone;
        if (Entry?.Parameter is Guid accountId) _ = LoadSubtitleAsync(accountId);

        var enabled = Bind.Switch(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, (s, v) => s.IsSignatureEnabled = v, Translator.SettingsSignature_Title);
        Add(Card(Translator.SettingsSignature_Title, Translator.SettingsSignature_EnableDescription, WinoIconGlyph.Signature, enabled));

        // Windows binds both defaults to the full list, including the "None" entry, and disables them while signatures are off.
        var forNew = Bind.PopUp(vm, s => s.Signatures, signature => signature.Name ?? string.Empty,
            nameof(vm.SelectedSignatureForNewMessages), s => s.SelectedSignatureForNewMessages, (s, v) => s.SelectedSignatureForNewMessages = v,
            itemsCollection: vm.Signatures);
        forNew.ContentDescription = Translator.SettingsSignature_ForNewMessages_Title;
        var forFollowing = Bind.PopUp(vm, s => s.Signatures, signature => signature.Name ?? string.Empty,
            nameof(vm.SelectedSignatureForFollowingMessages), s => s.SelectedSignatureForFollowingMessages, (s, v) => s.SelectedSignatureForFollowingMessages = v,
            itemsCollection: vm.Signatures);
        forFollowing.ContentDescription = Translator.SettingsSignature_ForFollowingMessages_Title;

        var newCard = StackedCard(Translator.SettingsSignature_ForNewMessages_Title, Translator.SettingsSignature_ForNewMessages_Description, WinoIconGlyph.NewMail, forNew);
        var followingCard = StackedCard(Translator.SettingsSignature_ForFollowingMessages_Title, Translator.SettingsSignature_ForFollowingMessages_Description, WinoIconGlyph.Reply, forFollowing);
        Bind.Bind(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, on => { newCard.IsEnabled = on; followingCard.IsEnabled = on; });
        AddGroup(Translator.SettingsSignature_SignatureDefaults, newCard, followingCard);

        // Section header with the accent Add button on its trailing edge.
        var title = Text(Translator.SettingsSignature_Signatures, WinoTextStyle.BodyLargeStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var add = Bind.Button(Translator.SettingsSignature_AddCustomSignature_Button, vm.OpenSignatureEditorCreateCommand, primary: true, icon: WinoIconGlyph.Add);
        var header = AccountSettingsParts.HRow(Ctx);
        header.AddView(title, AccountSettingsParts.RowItem(Ctx, weight: 1));
        header.AddView(add, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_s));
        Add(header);
        ((LinearLayout.LayoutParams)header.LayoutParameters!).TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l);

        _list = Add(AccountSettingsParts.VColumn(Ctx));
        _list.Background = AccountSettingsParts.Surface(Ctx, WinoColors.CardFill(Ctx), WinoColors.CardStroke(Ctx));
        _list.ClipToOutline = true;

        _empty = Add(EmptyState());

        Bind.Collection(vm.Signatures, QueueRebuild);
        // The "Default" pill follows the new-message default.
        Bind.Bind(vm, nameof(vm.SelectedSignatureForNewMessages), s => s.SelectedSignatureForNewMessages?.Id, _ => QueueRebuild());
        Rebuild();
    }

    private async Task LoadSubtitleAsync(Guid accountId)
    {
        try
        {
            if (MainApplication.Services?.GetService<IAccountService>() is not { } accounts) return;
            var account = await accounts.GetAccountAsync(accountId);
            if (account is null) return;
            var text = string.IsNullOrWhiteSpace(account.Address) || account.Address == account.Name ? account.Name : $"{account.Name} · {account.Address}";
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (Bindings.IsDisposed || _subtitle is null) return;
                _subtitle.Text = text ?? string.Empty;
                _subtitle.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
            });
        }
        catch (Exception exception) { ReportError(exception); }
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
        if (_list is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _list.RemoveAllViews();

        var defaultId = VM.SelectedSignatureForNewMessages?.Id;
        var signatures = VM.Signatures.Where(signature => signature.Id != VM.EmptyGuid).ToList();
        for (int index = 0; index < signatures.Count; index++)
        {
            if (index > 0)
            {
                var divider = new View(Ctx);
                divider.SetBackgroundColor(WinoColors.CardStroke(Ctx));
                _list.AddView(divider, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)));
            }
            var signature = signatures[index];
            AccountSettingsParts.AddStretched(_list, SignatureRow(rows, signature, signature.Id == defaultId));
        }

        // A loaded ViewModel always holds the synthetic "None"; before that, show neither the list nor the empty card.
        var loaded = VM.Signatures.Count > 0;
        _list.Visibility = signatures.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        if (_empty is not null) _empty.Visibility = !loaded || signatures.Count > 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>One list row: glyph, name with the optional Default pill, a one-line preview, then the actions menu.</summary>
    private View SignatureRow(SettingsBinder rows, AccountSignature signature, bool isDefault)
    {
        var name = signature.Name ?? string.Empty;
        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_card_min));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_sm);
        row.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Signature, Resource.Dimension.wino_glyph_l, WinoColors.SecondaryText(Ctx)));

        var titleLine = AccountSettingsParts.HRow(Ctx);
        titleLine.AddView(Text(name, WinoTextStyle.BodyLarge, maxLines: 1), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1));
        if (isDefault) titleLine.AddView(AccountSettingsParts.AccentPill(Ctx, Translator.SettingsSignature_DefaultBadge), AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_s));

        var text = AccountSettingsParts.VColumn(Ctx);
        text.AddView(titleLine);
        var previewText = Preview(signature.HtmlBody);
        if (!string.IsNullOrEmpty(previewText)) text.AddView(Text(previewText, WinoTextStyle.Description, maxLines: 1));
        row.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m));

        row.AddView(AccountSettingsParts.MoreButton(Ctx, rows, $"{Translator.More}: {name}",
        [
            new(Translator.Buttons_Edit, () => AccountSettingsParts.Execute(VM.OpenSignatureEditorEditCommand, signature, ReportError)),
            new(Translator.Buttons_Delete, () => AccountSettingsParts.Execute(VM.DeleteSignatureCommand, signature, ReportError), IsDestructive: true)
        ]));
        row.ContentDescription = isDefault ? $"{name}, {Translator.SettingsSignature_DefaultBadge}" : name;
        return row;
    }

    /// <summary>Dashed card with the Signature glyph in a 44 dp disc, shown instead of the list.</summary>
    private View EmptyState()
    {
        var discFill = new global::Android.Graphics.Drawables.GradientDrawable();
        discFill.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
        discFill.SetColor(WinoColors.ZoneFill(Ctx));
        discFill.SetStroke(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline), WinoColors.CardStroke(Ctx));
        var disc = new FrameLayout(Ctx) { Background = discFill };
        disc.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Signature, Resource.Dimension.wino_glyph_xl, WinoColors.SecondaryText(Ctx)),
            new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));

        var title = Text(Translator.SettingsSignature_EmptyTitle, WinoTextStyle.BodyStrong);
        title.Gravity = GravityFlags.Center;
        var description = Text(Translator.SettingsSignature_EmptyDescription, WinoTextStyle.Description);
        description.Gravity = GravityFlags.Center;

        var stack = AccountSettingsParts.VColumn(Ctx);
        stack.SetGravity(GravityFlags.CenterHorizontal);
        var discSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch);
        stack.AddView(disc, new LinearLayout.LayoutParams(discSize, discSize));
        stack.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_sm) });
        stack.AddView(description, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_xs) });
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_3xl, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_3xl);
        stack.Background = AccountSettingsParts.Surface(Ctx, WinoColors.CardFill(Ctx),
            WinoTokens.Color(Ctx, Resource.Color.wino_strong_stroke), dashed: true);
        stack.Focusable = true;
        stack.ContentDescription = $"{Translator.SettingsSignature_EmptyTitle}. {Translator.SettingsSignature_EmptyDescription}";
        stack.Visibility = ViewStates.Gone;
        return stack;
    }

    /// <summary>The signature's text on one line: block breaks become " · ", markup and entities are removed.</summary>
    internal static string Preview(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = BlockBreaks().Replace(text, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Split('\n')
            .Select(line => Whitespace().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join(" · ", lines);
    }

    [GeneratedRegex(@"<(style|script|head)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6]|blockquote|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
