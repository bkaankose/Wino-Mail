using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Presentation.Android;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>Signed-out state: the account header, the offers list and the selected offer's detail panel.</summary>
public sealed partial class WinoAccountManagementPageFragment
{
    private const int BenefitSpacing = Resource.Dimension.wino_space_2xs;
    private readonly List<WinoAccountBenefitTileView> _benefitTiles = [];

    private View SignedOutPanel()
    {
        var vm = VM;
        var panel = Panel();
        AddTo(panel, SignedOutHeader());
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_BenefitsSectionHeader));
        AddTo(panel, BenefitsGrid());
        var detail = new WinoAccountBenefitDetailPanel(Ctx);
        AddTo(panel, detail, Resource.Dimension.wino_space_s);

        Bind.Views.Click(detail.CallToAction, () =>
        {
            var command = vm.SelectedBenefit?.CtaCommand;
            if (command?.CanExecute(null) == true) command.Execute(null);
        });
        Bind.Bind(vm, nameof(vm.SelectedBenefit), s => s.SelectedBenefit, benefit =>
        {
            foreach (var tile in _benefitTiles) tile.IsSelected = ReferenceEquals(tile.Benefit, benefit);
            detail.Show(benefit);
        });
        return panel;
    }

    /// <summary>
    /// The Windows account row: the product mark, then the title with one line of context, the privacy note and
    /// Sign in (default) / Create account below them.
    /// </summary>
    private View SignedOutHeader()
    {
        var vm = VM;
        var mark = new ImageView(Ctx) { ImportantForAccessibility = ImportantForAccessibility.No };
        mark.SetImageResource(Resource.Drawable.wino_logo);
        mark.SetScaleType(ImageView.ScaleType.FitCenter);
        int markSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_winoaccount_logo);
        mark.LayoutParameters = new LinearLayout.LayoutParams(markSize, markSize);

        var title = Text(Translator.WinoAccount_Management_SignedOutTitle, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var description = Text(Translator.WinoAccount_Management_SignedOutDescription_Device, WinoTextStyle.Description);
        var privacy = Text(Translator.WinoAccount_Management_SignedOutPrivacyNote, WinoTextStyle.Caption);
        privacy.SetTextColor(WinoColors.TertiaryText(Ctx));
        var signIn = Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand, primary: true);
        var register = Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand);
        var buttons = IntelligenceViews.Flow(Ctx, [signIn, register], Resource.Dimension.wino_space_s);

        var text = Stack(Resource.Dimension.wino_space_3xs, title);
        AddTo(text, description, Resource.Dimension.wino_space_3xs);
        AddTo(text, privacy, Resource.Dimension.wino_space_xs);
        AddTo(text, buttons, Resource.Dimension.wino_space_m);

        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.AddView(mark);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l) });
        return Surface(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_small);
    }

    /// <summary>
    /// The four offers as selectable tiles (Windows ItemsView). The Mac lays them out two per row; phone width keeps
    /// one per row so the captions stay readable.
    /// </summary>
    private View BenefitsGrid()
    {
        var vm = VM;
        var grid = Panel(BenefitSpacing);
        _benefitTiles.Clear();
        foreach (var benefit in vm.Benefits.ToList())
        {
            var tile = new WinoAccountBenefitTileView(Ctx, benefit);
            EventHandler selected = (_, _) => vm.SelectedBenefit = tile.Benefit;
            tile.Selected += selected;
            Bindings.Own(new ActionDisposable(() => tile.Selected -= selected));
            _benefitTiles.Add(tile);
            AddTo(grid, tile, BenefitSpacing);
        }
        grid.ContentDescription = Translator.WinoAccount_Management_BenefitsSectionHeader;
        return grid;
    }
}
