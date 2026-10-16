using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Signed-out state: the account header, the offers list and the selected offer's detail panel.</summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private readonly List<WinoAccountBenefitTileView> _benefitTiles = [];

    private UIView SignedOutPanel()
    {
        var panel = Panel();
        AddTo(panel, SignedOutHeader());
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_BenefitsSectionHeader));
        var grid = AddTo(panel, BenefitsGrid());
        var detail = AddTo(panel, new WinoAccountBenefitDetailPanel());
        panel.SetCustomSpacing(8, grid);

        var vm = ViewModel;
        Bind.OnActivated(detail.CallToAction, () =>
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
    /// The Windows account row: the product mark, then the title with one line of context, the privacy
    /// note and Sign in (default) / Create account below them.
    /// </summary>
    private UIView SignedOutHeader()
    {
        var vm = ViewModel;
        var mark = new UIImageView(WelcomePageV2.LoadLogo()) { ContentMode = UIViewContentMode.ScaleAspectFit, TranslatesAutoresizingMaskIntoConstraints = false };
        mark.IsAccessibilityElement = false;
        WinoLayout.Size(mark, 56, 56);

        var title = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var description = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutDescription_Device, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var privacy = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutPrivacyNote, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
        var signIn = Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand, primary: true);
        var register = Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand);
        foreach (var button in new[] { signIn, register })
        {
            button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            // One line each: the spacer gives way before a button title wraps.
            WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 750, 1000);
        }
        var buttons = WinoLayout.HStack(8, signIn, register, WinoLayout.Spacer());

        var text = WinoLayout.VStack(2, title, description, privacy, buttons);
        text.Alignment = UIStackViewAlignment.Fill;
        text.SetCustomSpacing(6, description);
        text.SetCustomSpacing(12, privacy);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        var row = WinoLayout.HStack(16, mark, text);
        row.Alignment = UIStackViewAlignment.Top;
        return Surface(row, 16, 4);
    }

    /// <summary>
    /// The four offers as selectable tiles (Windows ItemsView). The Mac lays them out two per row; phone width keeps
    /// one per row so the captions stay readable.
    /// </summary>
    private UIView BenefitsGrid()
    {
        var vm = ViewModel;
        var grid = Panel(BenefitSpacing);
        _benefitTiles.Clear();
        foreach (var benefit in vm.Benefits.ToList())
        {
            var tile = new WinoAccountBenefitTileView(benefit);
            EventHandler selected = (_, _) => vm.SelectedBenefit = tile.Benefit;
            tile.Selected += selected;
            Bindings.Own(new Infrastructure.ActionDisposable(() => tile.Selected -= selected));
            _benefitTiles.Add(tile);
            AddTo(grid, tile);
        }
        grid.AccessibilityLabel = Translator.WinoAccount_Management_BenefitsSectionHeader;
        return grid;
    }

    private const double BenefitSpacing = 4;
}
