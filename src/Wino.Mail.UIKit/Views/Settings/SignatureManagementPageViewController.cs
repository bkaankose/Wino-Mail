using System.Net;
using System.Text.RegularExpressions;
using CoreAnimation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Signatures of one account (Mac SignatureManagementPageViewController, Windows SignatureManagementPage): the
/// Signature switch, the two default menus (the ViewModel's synthetic "None" first), then the Signatures header with
/// Add signature and one joined card listing each signature with a "Default" pill for the new-message default, a
/// one-line text preview and an actions menu with Edit and Delete (inline buttons on the Mac). With no signatures a
/// dashed empty card takes the list's place. Navigation parameter: the account id.
/// </summary>
public sealed partial class SignatureManagementPageViewController(SignatureManagementPageViewModel viewModel, IAccountService accountService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<SignatureManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly UIStackView _list = WinoLayout.VStack(0);
    private readonly UILabel _subtitle = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.SecondaryText);
    private readonly WinoSurfaceView _listCard = new() { CornerRadius = WinoSettingsStyle.CardRadius, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1 };
    private UIView? _empty;
    private UIView? _subtitleHost;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.SettingsSignature_Title;
    /// <summary>The title is fixed.</summary>
    public event EventHandler? PageTitleChanged { add { } remove { } }

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // "Account name · address" under the sheet's title.
        _subtitle.LineBreakMode = UILineBreakMode.TailTruncation;
        var subtitleHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_subtitle, subtitleHost, 0, 1, 10, 0);
        _subtitleHost = Add(subtitleHost);

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
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        WinoLayout.Priorities(title, UILayoutConstraintAxis.Horizontal, 1, 250);
        var add = Bind.Button(Translator.SettingsSignature_AddCustomSignature_Button, vm.OpenSignatureEditorCreateCommand, primary: true, icon: WinoIconGlyph.Add);
        add.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var header = WinoLayout.HStack(WinoStyle.Space2, title, add);
        WinoLayout.Insets(header, 18, 1, 2, 0);
        Add(header);

        _list.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(_list, _listCard);
        Add(_listCard);

        _empty = Add(EmptyState());

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
        await ViewModel.InitializeAsync(mode, parameter);
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
                _subtitle.Text = text ?? string.Empty;
                if (_subtitleHost is not null) _subtitleHost.Hidden = string.IsNullOrWhiteSpace(text);
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
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
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
            AddListView(SignatureRow(signature, signature.Id == defaultId));
        }

        // A loaded ViewModel always holds the synthetic "None"; before that, show neither the list nor the empty card.
        var loaded = ViewModel.Signatures.Count > 0;
        _listCard.Hidden = signatures.Count == 0;
        if (_empty is not null) _empty.Hidden = !loaded || signatures.Count > 0;
    }

    private void AddListView(UIView view, double height = -1)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _list.AddArrangedSubview(view);
        if (height > 0) view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
    }

    /// <summary>One list row: glyph, name with the optional Default pill, a one-line preview, then the actions menu.</summary>
    private UIView SignatureRow(AccountSignature signature, bool isDefault)
    {
        var name = signature.Name ?? string.Empty;
        var icon = new WinoIconView(WinoIconGlyph.Signature, WinoSettingsStyle.IconSize);
        WinoLayout.Size(icon, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);

        var nameLabel = WinoStyle.Label(name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText);
        nameLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(nameLabel, UILayoutConstraintAxis.Horizontal, 250, 250);
        var titleLine = WinoLayout.HStack(WinoStyle.Space2, nameLabel);
        if (isDefault) titleLine.AddArrangedSubview(DefaultPill());

        var previewText = Preview(signature.HtmlBody);
        var preview = WinoStyle.Label(previewText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        preview.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(preview, UILayoutConstraintAxis.Horizontal, 1, 1);
        preview.Hidden = string.IsNullOrEmpty(previewText);

        var text = WinoLayout.VStack(1, titleLine, preview);
        text.Alignment = UIStackViewAlignment.Leading;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        preview.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;
        titleLine.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        var edit = UIAction.Create(Translator.Buttons_Edit, WinoIcons.Image(WinoIconGlyph.Edit, 17), null, _ => Execute(ViewModel.OpenSignatureEditorEditCommand, signature));
        var delete = UIAction.Create(Translator.Buttons_Delete, WinoIcons.Image(WinoIconGlyph.Delete, 17, WinoStyle.Critical), null, _ => Execute(ViewModel.DeleteSignatureCommand, signature));
        delete.Attributes = UIMenuElementAttributes.Destructive;
        var more = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, ShowsMenuAsPrimaryAction = true };
        more.SetImage(WinoIcons.Image(WinoIconGlyph.More, 17, WinoStyle.SecondaryText), UIControlState.Normal);
        more.Menu = UIMenu.Create([edit, delete]);
        WinoLayout.Size(more, 44, 44);
        WinoAccessibility.Label(more, $"{Translator.More}: {name}");

        var row = WinoLayout.HStack(WinoSettingsStyle.IconGap, icon, text, more);
        WinoLayout.Insets(row, WinoSettingsStyle.CardVerticalPadding, WinoSettingsStyle.CardPadding, WinoSettingsStyle.CardVerticalPadding, 6);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight).Active = true;
        row.AccessibilityLabel = isDefault ? $"{name}, {Translator.SettingsSignature_DefaultBadge}" : name;
        return row;
    }

    /// <summary>The accent "Default" capsule: 18pt tall, 12% accent fill, accent text.</summary>
    private static UIView DefaultPill()
    {
        var pill = new WinoSurfaceView { CornerRadius = 9, Fill = WinoStyle.Accent.ColorWithAlpha(0.12f), TranslatesAutoresizingMaskIntoConstraints = false };
        var label = WinoStyle.Label(Translator.SettingsSignature_DefaultBadge, WinoStyle.CaptionStrong, WinoStyle.Accent);
        label.TranslatesAutoresizingMaskIntoConstraints = false;
        pill.AddSubview(label);
        NSLayoutConstraint.ActivateConstraints(
        [
            pill.HeightAnchor.ConstraintGreaterThanOrEqualTo(18),
            label.LeadingAnchor.ConstraintEqualTo(pill.LeadingAnchor, 7),
            label.TrailingAnchor.ConstraintEqualTo(pill.TrailingAnchor, -7),
            label.TopAnchor.ConstraintEqualTo(pill.TopAnchor, 1),
            label.BottomAnchor.ConstraintEqualTo(pill.BottomAnchor, -1)
        ]);
        WinoLayout.Priorities(pill, UILayoutConstraintAxis.Horizontal, 750, 750);
        pill.IsAccessibilityElement = false;
        return pill;
    }

    /// <summary>Dashed card with the Signature glyph in a 44pt disc, shown instead of the list.</summary>
    private static UIView EmptyState()
    {
        var card = new DashedCardView();
        var disc = new WinoSurfaceView { CornerRadius = 22, Fill = WinoStyle.ZoneFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1 };
        WinoLayout.Size(disc, 44, 44);
        var glyph = new WinoIconView(WinoIconGlyph.Signature, 22, WinoStyle.SecondaryText) { TranslatesAutoresizingMaskIntoConstraints = false };
        disc.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints([glyph.CenterXAnchor.ConstraintEqualTo(disc.CenterXAnchor), glyph.CenterYAnchor.ConstraintEqualTo(disc.CenterYAnchor)]);

        var title = WinoStyle.Label(Translator.SettingsSignature_EmptyTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        title.TextAlignment = UITextAlignment.Center;
        var description = WinoStyle.Label(Translator.SettingsSignature_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        description.TextAlignment = UITextAlignment.Center;
        description.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;

        var stack = WinoLayout.VStack(6, disc, title, description);
        stack.Alignment = UIStackViewAlignment.Center;
        stack.SetCustomSpacing(10, disc);
        WinoLayout.Insets(stack, 28, 16, 28, 16);
        WinoLayout.Fill(stack, card);
        card.IsAccessibilityElement = true;
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

    private void Execute(System.Windows.Input.ICommand command, object? parameter)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>A card-fill rounded rectangle with a dashed hairline border (the empty state).</summary>
    private sealed class DashedCardView : UIView
    {
        private readonly CAShapeLayer _border = new() { LineWidth = 1, LineDashPattern = [4, 3] };

        public DashedCardView()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Layer.AddSublayer(_border);
            ApplyColors();
            WinoTraits.OnAppearanceChanged(this, static view => view.ApplyColors());
        }

        private static UIColor DashStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.14), WinoStyle.Hex(0xFFFFFF, 0.16));

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            _border.Frame = Bounds;
            _border.Path = UIBezierPath.FromRoundedRect(Bounds.Inset(0.5f, 0.5f), (nfloat)WinoSettingsStyle.CardRadius).CGPath;
        }

        private void ApplyColors()
        {
            _border.FillColor = WinoSettingsStyle.CardFill.GetResolvedColor(TraitCollection).CGColor;
            _border.StrokeColor = DashStroke.GetResolvedColor(TraitCollection).CGColor;
        }
    }
}
