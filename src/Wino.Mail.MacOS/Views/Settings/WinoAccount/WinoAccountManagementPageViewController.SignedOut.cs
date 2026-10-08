using AppKit;
using Wino.Core.Domain;
using Wino.Core.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Signed-out state: the account header, the offers grid and the selected offer's detail panel.</summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private readonly List<WinoAccountBenefitTileView> _benefitTiles = [];

    private NSView SignedOutPanel()
    {
        var panel = Panel();
        AddTo(panel, SignedOutHeader());
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_BenefitsSectionHeader));
        AddTo(panel, BenefitsGrid());
        var detail = AddTo(panel, new WinoAccountBenefitDetailPanel());
        panel.SetCustomSpacing(8, panel.ArrangedSubviews[^2]);

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
    /// The Windows account row: the product mark, the title with one line of context and the privacy
    /// note, then Sign in (default) and Create account.
    /// </summary>
    private NSView SignedOutHeader()
    {
        var vm = ViewModel;
        var mark = new NSImageView
        {
            Image = NSImage.ImageNamed("AppIcon") ?? NSApplication.SharedApplication.ApplicationIconImage,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        mark.AccessibilityElement = false;
        WinoLayout.Size(mark, 56, 56);

        var title = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var description = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutDescription, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var privacy = WinoStyle.Label(Translator.WinoAccount_Management_SignedOutPrivacyNote, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
        var text = WinoLayout.VStack(2, title, description, privacy);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetCustomSpacing(6, description);
        foreach (var label in new[] { title, description, privacy })
        {
            label.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
            label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        }
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        var signIn = Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand, primary: true);
        var register = Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand);
        var buttons = WinoLayout.HStack(8, signIn, register);
        buttons.SetHuggingPriority(751, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(16, mark, text, buttons);
        row.Alignment = NSLayoutAttribute.CenterY;
        return Surface(row, 16, 4);
    }

    /// <summary>The four offers as a two-column grid of selectable tiles (Windows ItemsView, UniformGridLayout).</summary>
    private NSView BenefitsGrid()
    {
        var vm = ViewModel;
        var grid = Panel(BenefitSpacing);
        _benefitTiles.Clear();
        var benefits = vm.Benefits.ToList();
        for (var index = 0; index < benefits.Count; index += 2)
        {
            var row = WinoLayout.HStack(BenefitSpacing);
            row.Distribution = NSStackViewDistribution.FillEqually;
            row.Alignment = NSLayoutAttribute.Top;
            foreach (var benefit in benefits.Skip(index).Take(2))
            {
                var tile = new WinoAccountBenefitTileView(benefit);
                tile.Selected += (_, _) => vm.SelectedBenefit = tile.Benefit;
                _benefitTiles.Add(tile);
                row.AddArrangedSubview(tile);
            }
            // An odd last offer keeps the column width of the others.
            if (row.ArrangedSubviews.Length == 1) row.AddArrangedSubview(new NSView { TranslatesAutoresizingMaskIntoConstraints = false });
            AddTo(grid, row);
        }
        WinoAccessibility.Label(grid, Translator.WinoAccount_Management_BenefitsSectionHeader);
        grid.AccessibilityElement = true;
        grid.AccessibilityRole = NSAccessibilityRoles.RadioGroupRole;
        return grid;
    }

    private const double BenefitSpacing = 4;
}
