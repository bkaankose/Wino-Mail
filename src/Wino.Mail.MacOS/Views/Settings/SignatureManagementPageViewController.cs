using System.Net;
using System.Text.RegularExpressions;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Signatures of one account (Manage accounts › account › Signature; Windows SignatureManagementPage):
/// the Signature switch, the two default pop-ups (the ViewModel's synthetic "None" first), then the
/// Signatures header with Add signature and one joined card listing each signature with a "Default"
/// pill for the new-message default, a one-line text preview, Edit and Delete. With no signatures a
/// dashed empty card takes the list's place. Navigation parameter: the account id.
/// </summary>
public sealed partial class SignatureManagementPageViewController(SignatureManagementPageViewModel viewModel, IAccountService accountService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<SignatureManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly NSStackView _list = WinoLayout.VStack(0);
    private NSTextField _subtitle = null!;
    private WinoSurfaceView _listCard = null!;
    private NSView _empty = null!;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.SettingsSignature_Title;
    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // "Account name · address" under the window's breadcrumb title.
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.SecondaryText);
        _subtitle.Hidden = true;
        var subtitleHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_subtitle, subtitleHost, 0, 1, 10, 0);
        Add(subtitleHost);

        var enabled = Bind.Switch(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, (s, v) => s.IsSignatureEnabled = v, Translator.SettingsSignature_Title);
        Add(Card(Translator.SettingsSignature_Title, Translator.SettingsSignature_EnableDescription, WinoIconGlyph.Signature, enabled));

        // Windows binds both defaults to the full list, including the "None" entry, and disables them while signatures are off.
        var forNew = Bind.PopUp(vm, s => s.Signatures, signature => signature.Name ?? string.Empty,
            nameof(vm.SelectedSignatureForNewMessages), s => s.SelectedSignatureForNewMessages, (s, v) => s.SelectedSignatureForNewMessages = v,
            width: WinoSettingsStyle.PopUpMinWidth, itemsCollection: vm.Signatures);
        WinoAccessibility.Label(forNew, Translator.SettingsSignature_ForNewMessages_Title);
        var forFollowing = Bind.PopUp(vm, s => s.Signatures, signature => signature.Name ?? string.Empty,
            nameof(vm.SelectedSignatureForFollowingMessages), s => s.SelectedSignatureForFollowingMessages, (s, v) => s.SelectedSignatureForFollowingMessages = v,
            width: WinoSettingsStyle.PopUpMinWidth, itemsCollection: vm.Signatures);
        WinoAccessibility.Label(forFollowing, Translator.SettingsSignature_ForFollowingMessages_Title);

        var newCard = Card(Translator.SettingsSignature_ForNewMessages_Title, Translator.SettingsSignature_ForNewMessages_Description, WinoIconGlyph.NewMail, forNew);
        var followingCard = Card(Translator.SettingsSignature_ForFollowingMessages_Title, Translator.SettingsSignature_ForFollowingMessages_Description, WinoIconGlyph.Reply, forFollowing);
        Bind.Bind(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, on => { newCard.IsEnabled = on; followingCard.IsEnabled = on; });
        AddGroup(Translator.SettingsSignature_SignatureDefaults, newCard, followingCard);

        // Section header with the accent Add button on its trailing edge.
        var title = WinoStyle.Label(Translator.SettingsSignature_Signatures, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        var add = Bind.Button(Translator.SettingsSignature_AddCustomSignature_Button, vm.OpenSignatureEditorCreateCommand, primary: true, icon: WinoIconGlyph.Add);
        var header = WinoLayout.HStack(WinoStyle.Space2, title, WinoLayout.Spacer(), add);
        header.EdgeInsets = new NSEdgeInsets(18, 1, 2, 0);
        Add(header);

        _listCard = new WinoSurfaceView { CornerRadius = WinoSettingsStyle.CardRadius, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1 };
        _list.Alignment = NSLayoutAttribute.Leading;
        WinoLayout.Fill(_list, _listCard);
        Add(_listCard);

        _empty = EmptyState();
        Add(_empty);

        Bind.Collection(vm.Signatures, QueueRebuild);
        // The "Default" pill follows the new-message default.
        Bind.Bind(vm, nameof(vm.SelectedSignatureForNewMessages), s => s.SelectedSignatureForNewMessages?.Id, _ => QueueRebuild());
        EventHandler accentChanged = (_, _) => QueueRebuild();
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));
        Rebuild();
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        if (parameter is Guid accountId) _ = LoadSubtitleAsync(accountId);
        await ViewModel.InitializeAsync(mode, parameter!);
    }

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    private async Task LoadSubtitleAsync(Guid accountId)
    {
        try
        {
            var account = await accountService.GetAccountAsync(accountId);
            if (account is null) return;
            var text = string.IsNullOrWhiteSpace(account.Address) || account.Address == account.Name ? account.Name : $"{account.Name} · {account.Address}";
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _subtitle.StringValue = text ?? string.Empty;
                _subtitle.Hidden = string.IsNullOrWhiteSpace(text);
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        foreach (var view in _list.ArrangedSubviews)
        {
            _list.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        var defaultId = ViewModel.SelectedSignatureForNewMessages?.Id;
        var signatures = ViewModel.Signatures.Where(signature => signature.Id != ViewModel.EmptyGuid).ToList();
        for (int index = 0; index < signatures.Count; index++)
        {
            if (index > 0) AddListView(new WinoSurfaceView { Fill = WinoSettingsStyle.CardStroke, TranslatesAutoresizingMaskIntoConstraints = false }, 1);
            var signature = signatures[index];
            AddListView(SignatureRow(rows, signature, signature.Id == defaultId));
        }

        // A loaded ViewModel always holds the synthetic "None"; before that, show neither the list nor the empty card.
        var loaded = ViewModel.Signatures.Count > 0;
        _listCard.Hidden = signatures.Count == 0;
        _empty.Hidden = !loaded || signatures.Count > 0;
    }

    private void AddListView(NSView view, double height = -1)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _list.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
        if (height > 0) view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
    }

    /// <summary>One list row: glyph, name with the optional Default pill, a one-line preview, Edit and Delete.</summary>
    private NSView SignatureRow(SettingsBinder rows, AccountSignature signature, bool isDefault)
    {
        var name = signature.Name ?? string.Empty;
        var icon = new WinoIconView(WinoIconGlyph.Signature, WinoSettingsStyle.IconSize);
        WinoLayout.Size(icon, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);

        var nameLabel = WinoStyle.Label(name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText);
        nameLabel.LineBreakMode = NSLineBreakMode.TruncatingTail;
        nameLabel.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var titleLine = WinoLayout.HStack(WinoStyle.Space2, nameLabel);
        if (isDefault) titleLine.AddArrangedSubview(DefaultPill());

        var preview = WinoStyle.Label(Preview(signature.HtmlBody), WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        preview.LineBreakMode = NSLineBreakMode.TruncatingTail;
        preview.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        preview.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        preview.Hidden = string.IsNullOrEmpty(preview.StringValue);

        var text = WinoLayout.VStack(1, titleLine, preview);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        preview.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        var edit = rows.Button(Translator.Buttons_Edit, ViewModel.OpenSignatureEditorEditCommand, () => signature);
        WinoAccessibility.Label(edit, $"{Translator.SettingsSignature_EditSignature_Title}: {name}");
        var delete = rows.Button(Translator.Buttons_Delete, ViewModel.DeleteSignatureCommand, () => signature);
        delete.AttributedTitle = new Foundation.NSAttributedString(Translator.Buttons_Delete, new NSStringAttributes { ForegroundColor = WinoStyle.Critical, Font = delete.Font });
        WinoAccessibility.Label(delete, $"{Translator.SettingsSignature_DeleteSignature_Title}: {name}");

        var row = WinoLayout.HStack(WinoSettingsStyle.IconGap, icon, text, WinoLayout.HStack(WinoStyle.Space2, edit, delete));
        row.EdgeInsets = new NSEdgeInsets((nfloat)WinoSettingsStyle.CardVerticalPadding, (nfloat)WinoSettingsStyle.CardPadding, (nfloat)WinoSettingsStyle.CardVerticalPadding, (nfloat)WinoSettingsStyle.CardPadding);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight).Active = true;
        row.AccessibilityElement = true;
        row.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        row.AccessibilityLabel = isDefault ? $"{name}, {Translator.SettingsSignature_DefaultBadge}" : name;
        return row;
    }

    /// <summary>The accent "Default" capsule: 18pt tall, 12% accent fill, accent text.</summary>
    private static NSView DefaultPill()
    {
        var pill = new WinoSurfaceView { CornerRadius = 9, Fill = WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.12) };
        var label = WinoStyle.Label(Translator.SettingsSignature_DefaultBadge, WinoStyle.CaptionStrong, WinoStyle.Accent);
        label.TranslatesAutoresizingMaskIntoConstraints = false;
        pill.AddSubview(label);
        NSLayoutConstraint.ActivateConstraints(
        [
            pill.HeightAnchor.ConstraintEqualTo(18),
            label.LeadingAnchor.ConstraintEqualTo(pill.LeadingAnchor, 7),
            label.TrailingAnchor.ConstraintEqualTo(pill.TrailingAnchor, -7),
            label.CenterYAnchor.ConstraintEqualTo(pill.CenterYAnchor)
        ]);
        pill.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        pill.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        pill.AccessibilityElement = false;
        return pill;
    }

    /// <summary>Dashed card with the Signature glyph in a 44pt disc, shown instead of the list.</summary>
    private static NSView EmptyState()
    {
        var card = new DashedCardView();
        var disc = new WinoSurfaceView { CornerRadius = 22, Fill = WinoStyle.ZoneFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1 };
        WinoLayout.Size(disc, 44, 44);
        var glyph = new WinoIconView(WinoIconGlyph.Signature, 22, WinoStyle.SecondaryText);
        disc.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints([glyph.CenterXAnchor.ConstraintEqualTo(disc.CenterXAnchor), glyph.CenterYAnchor.ConstraintEqualTo(disc.CenterYAnchor)]);

        var title = WinoStyle.Label(Translator.SettingsSignature_EmptyTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var description = WinoStyle.Label(Translator.SettingsSignature_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        description.Alignment = NSTextAlignment.Center;
        description.PreferredMaxLayoutWidth = 360;
        description.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;

        var stack = WinoLayout.VStack(6, disc, title, description);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.SetCustomSpacing(10, disc);
        stack.EdgeInsets = new NSEdgeInsets(28, 16, 28, 16);
        WinoLayout.Fill(stack, card);
        card.AccessibilityElement = true;
        card.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        card.AccessibilityLabel = $"{Translator.SettingsSignature_EmptyTitle}. {Translator.SettingsSignature_EmptyDescription}";
        return card;
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

    /// <summary>A card-fill rounded rectangle with a dashed hairline border (the empty state).</summary>
    private sealed class DashedCardView : NSView
    {
        public DashedCardView() => TranslatesAutoresizingMaskIntoConstraints = false;

        private static NSColor DashStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.14), WinoStyle.Hex(0xFFFFFF, 0.16));

        public override void DrawRect(CGRect dirtyRect)
        {
            var rect = Bounds.Inset((nfloat)0.5, (nfloat)0.5);
            var radius = (nfloat)WinoSettingsStyle.CardRadius;
            var path = NSBezierPath.FromRoundedRect(rect, radius, radius);
            WinoSettingsStyle.CardFill.SetFill();
            path.Fill();
            DashStroke.SetStroke();
            path.LineWidth = 1;
            path.SetLineDash(new nfloat[] { 4, 3 }, 0);
            path.Stroke();
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            NeedsDisplay = true;
        }
    }
}
