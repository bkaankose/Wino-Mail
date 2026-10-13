using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Onboarding;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views;

/// <summary>
/// Provider selection wizard (Windows ProviderSelectionPage, Mac ProviderSelectionPageViewController): featured provider
/// tiles and an "IMAP / SMTP" row that opens the searchable catalog, then the account identity, then the capabilities.
/// Continue follows the shared ViewModel, which routes OAuth to account setup, catalog providers to their credentials
/// page and custom servers to the server page.
/// </summary>
public sealed class ProviderSelectionPageViewController(ProviderSelectionPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ProviderSelectionPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly List<ProviderCard> _cards = new();
    private readonly UIStackView _featuredTiles = WinoLayout.VStack(10);
    private readonly UIStackView _customTiles = WinoLayout.VStack(10);
    private readonly UIStackView _catalogTiles = WinoLayout.VStack(10);
    private readonly List<Action> _catalogRefresh = new();
    private readonly WinoColorSwatchPicker _colors = new();
    private FormBinder<ProviderSelectionPageViewModel>? _form;

    private FormBinder<ProviderSelectionPageViewModel> Form => _form ??= new FormBinder<ProviderSelectionPageViewModel>(ViewModel, Bindings, Dispatcher, ReportError);

    public override void LoadView()
    {
        var vm = ViewModel;
        var title = new UILabel();
        BindText(title, nameof(vm.PageTitle), s => s.PageTitle);
        var subtitle = new UILabel();
        BindText(subtitle, nameof(vm.PageSubtitle), s => s.PageSubtitle);
        var stepIndicator = new WizardStepIndicator(ProviderSelectionPageViewModel.TotalStepCount);
        Form.Bind(nameof(vm.StepProgressText), s => (s.CurrentStepNumber, s.StepProgressText), step => stepIndicator.Set(step.CurrentStepNumber, step.StepProgressText));

        var continueButton = CommandButton(Translator.Buttons_Continue, vm.ContinueCommand, primary: true);
        Form.Bind(nameof(vm.ContinueButtonText), s => s.ContinueButtonText, text => SetTitle(continueButton, text));
        var backButton = CommandButton(Translator.Buttons_Back, vm.GoBackCommand);
        Form.Visible(backButton, nameof(vm.CanGoBack), s => s.CanGoBack);

        var providerStep = BuildProviderStep();
        Form.Visible(providerStep, nameof(vm.IsProviderStepVisible), s => s.IsProviderStepVisible);
        var summary = BuildAccountSummary();
        var identity = BuildIdentityStep();
        Form.Visible(identity, nameof(vm.IsIdentityStepVisible), s => s.IsIdentityStepVisible);
        var capabilities = BuildCapabilityStep();
        Form.Visible(capabilities, nameof(vm.IsCapabilityStepVisible), s => s.IsCapabilityStepVisible);

        var content = WinoLayout.VStack(20, providerStep, summary, identity, capabilities);
        content.Alignment = UIStackViewAlignment.Fill;
        View = new OnboardingPageView(OnboardingPageView.Header(null, title, subtitle), content, stepIndicator, [backButton], continueButton, 720);
    }

    private static void SetTitle(UIButton button, string? text)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = text ?? string.Empty;
        button.Configuration = configuration;
    }

    private UIView BuildAccountSummary()
    {
        var vm = ViewModel;
        var panel = new AccountSummaryPanel();
        Form.Visible(panel, nameof(vm.IsAccountSummaryVisible), s => s.IsAccountSummaryVisible);
        Form.Bind(nameof(vm.SelectedProvider), s => s.SelectedProvider, provider =>
        {
            panel.SetProvider(provider);
            panel.Name = vm.SelectedProviderName;
        });
        Form.Bind(nameof(vm.SelectedProviderSummaryDetail), s => s.SelectedProviderSummaryDetail, detail => panel.Detail = detail);
        Form.Bind(nameof(vm.SelectedColor), s => s.SelectedColor?.Hex, hex =>
        {
            panel.ColorHex = hex;
            _colors.SelectedHex = hex;
        });
        EventHandler<string> selected = (_, hex) =>
        {
            var color = vm.AvailableColors.FirstOrDefault(item => string.Equals(item.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (color is not null) vm.SelectedColor = color;
        };
        _colors.SelectionChanged += selected;
        Bindings.Own(new ActionDisposable(() => _colors.SelectionChanged -= selected));
        return panel;
    }

    /// <summary>Account name and colour on one card (Windows IdentityStepPanel).</summary>
    private UIView BuildIdentityStep()
    {
        var vm = ViewModel;
        var name = Form.Text(nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v,
            Translator.ProviderSelection_AccountNamePlaceholder, Translator.ProviderSelection_AccountNameHeader);
        name.AutocapitalizationType = UITextAutocapitalizationType.Words;
        var grid = new FormGrid();
        grid.Row(Translator.ProviderSelection_AccountNameHeader, name);
        grid.Row((string?)null, FormGrid.Help(Translator.ProviderSelection_AccountNameDescription));
        grid.Row(Translator.AccountDetailsPage_ColorPicker_Title, _colors);
        grid.Row((string?)null, FormGrid.Help(Translator.AccountDetailsPage_ColorPicker_Description));
        return ProviderSelectionPage.Card(grid, WinoStyle.Space4);
    }

    /// <summary>What the account is used for: capability menus, the download range with its warning, and the step's hints.</summary>
    private UIView BuildCapabilityStep()
    {
        var vm = ViewModel;
        var heading = ProviderSelectionPage.Heading(Translator.ProviderSelection_CapabilitySectionTitle, Translator.ProviderSelection_CapabilityIntroDescription);
        var capabilities = new FormBinder<AccountCapabilitySelection>(vm.Capabilities, Bindings, Dispatcher, ReportError);
        var grid = new FormGrid();
        foreach (var (label, control) in FormBinder<AccountCapabilitySelection>.CapabilityRows(capabilities, () => vm.MailProviderModeLabel))
            grid.Row(label, control);

        var range = Form.PopUp(vm.InitialSynchronizationRanges.Select(option => option.DisplayText).ToArray(), nameof(vm.SelectedInitialSynchronizationRange),
            s => s.InitialSynchronizationRanges.IndexOf(s.SelectedInitialSynchronizationRange),
            (s, index) => { if (index >= 0 && index < s.InitialSynchronizationRanges.Count) s.SelectedInitialSynchronizationRange = s.InitialSynchronizationRanges[index]; },
            Translator.AccountCreation_InitialSynchronization_Title);
        Form.Bind(nameof(vm.IsMailSynchronizationRangeVisible), s => s.IsMailSynchronizationRangeVisible, enabled => range.Enabled = enabled);
        grid.Row(Translator.ProviderSelection_MailRangeHeader, range);
        grid.Row((string?)null, FormGrid.Help(Translator.ProviderSelection_MailRangeDescription));
        var everythingWarning = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.GeneralTitle_Warning, Translator.AccountCreation_InitialSynchronization_EverythingWarning);
        Form.Visible(everythingWarning, nameof(vm.IsInitialSynchronizationWarningVisible), s => s.IsInitialSynchronizationWarningVisible);

        var calendarOnlyHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ProviderSelection_CalendarOnlyServerHint);
        Form.Visible(calendarOnlyHint, nameof(vm.IsCalendarOnlyServerHintVisible), s => s.IsCalendarOnlyServerHintVisible);
        var localOnlyHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ProviderSelection_LocalOnlyHint_Device);
        Form.Visible(localOnlyHint, nameof(vm.IsLocalOnlyHintVisible), s => s.IsLocalOnlyHintVisible);
        var cardDavHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, vm.DavContactAvailabilityMessage);
        Form.Visible(cardDavHint, nameof(vm.IsCardDavDiscoveryHintVisible), s => s.IsCardDavDiscoveryHintVisible);
        var missing = WinoStyle.Label(Translator.ProviderSelection_CapabilityValidationMessage, WinoStyle.Caption, WinoStyle.Critical, 0);
        Form.Visible(missing, nameof(vm.IsCapabilitySelectionMissing), s => s.IsCapabilitySelectionMissing);

        var step = WinoLayout.VStack(WinoStyle.Space3, heading, ProviderSelectionPage.Card(grid, WinoStyle.Space4), everythingWarning, calendarOnlyHint, localOnlyHint, cardDavHint, missing);
        step.Alignment = UIStackViewAlignment.Fill;
        return step;
    }

    /// <summary>Featured tiles with the IMAP / SMTP row, or the searchable catalog in their place.</summary>
    private UIView BuildProviderStep()
    {
        var vm = ViewModel;
        var more = new ProviderCard(WinoIconGlyph.ProviderCatalog, Translator.ProviderSelection_MoreProviders_Title,
            Translator.ProviderSelection_MoreProviders_Description, showsArrow: true);
        EventHandler showCatalog = (_, _) => vm.ShowCatalogCommand.Execute(null);
        more.Clicked += showCatalog;
        Bindings.Own(new ActionDisposable(() => more.Clicked -= showCatalog));
        var featured = ProviderSelectionPage.Card(Column(WinoStyle.Space3,
            ProviderSelectionPage.Heading(Translator.ProviderSelection_ProviderSectionTitle, Translator.ProviderSelection_ProviderSectionDescription),
            _featuredTiles, more));
        Form.Visible(featured, nameof(vm.IsFeaturedListVisible), s => s.IsFeaturedListVisible);

        var search = new UISearchTextField { Placeholder = Translator.ProviderSelection_Catalog_SearchPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        search.AccessibilityLabel = Translator.ProviderSelection_Catalog_SearchPlaceholder;
        var searchBinding = Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, string>(vm, nameof(vm.CatalogSearchText),
            s => s.CatalogSearchText ?? string.Empty, value => { if (search.Text != value) search.Text = value; }, Dispatcher, ReportError,
            (s, value) => s.CatalogSearchText = value));
        EventHandler searched = (_, _) => searchBinding.UpdateSource(search.Text ?? string.Empty);
        search.EditingChanged += searched;
        Bindings.Own(new ActionDisposable(() => search.EditingChanged -= searched));
        var count = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        _catalogRefresh.Add(Form.Bind(nameof(vm.CatalogResultCountText), s => s.CatalogResultCountText, text => count.Text = text ?? string.Empty).Refresh);
        var empty = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Form.Label(empty, nameof(vm.CatalogNoResultsText), s => s.CatalogNoResultsText);
        Form.Visible(empty, nameof(vm.IsCatalogEmpty), s => s.IsCatalogEmpty);
        var catalog = ProviderSelectionPage.Card(Column(WinoStyle.Space3,
            ProviderSelectionPage.Heading(Translator.ProviderSelection_Catalog_SectionTitle, Translator.ProviderSelection_Catalog_SectionDescription),
            _customTiles, ProviderSelectionPage.Divider(Translator.ProviderSelection_Catalog_KnownProvidersHeader), search, count, _catalogTiles, empty));
        Form.Visible(catalog, nameof(vm.IsCatalogVisible), s => s.IsCatalogVisible);

        Form.Bind(nameof(vm.FeaturedProviders), s => s.FeaturedProviders, providers => Fill(_featuredTiles, providers));
        Form.Bind(nameof(vm.CustomServerProviders), s => s.CustomServerProviders, providers => Fill(_customTiles, providers));
        _catalogRefresh.Add(Form.Bind(nameof(vm.FilteredCatalogProviders), s => s.FilteredCatalogProviders, providers => Fill(_catalogTiles, providers)).Refresh);
        Form.Bind(nameof(vm.SelectedProvider), s => s.SelectedProvider, UpdateSelection);
        return Column(WinoStyle.Space4, featured, catalog);
    }

    /// <summary>Replaces the tiles of one list, one tile per row (iPhone width).</summary>
    private void Fill(UIStackView host, IReadOnlyList<IProviderDetail>? providers)
    {
        foreach (var view in host.ArrangedSubviews)
        {
            if (view is ProviderCard card) _cards.Remove(card);
            host.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        host.Alignment = UIStackViewAlignment.Fill;
        foreach (var provider in providers ?? [])
        {
            var card = new ProviderCard(provider) { IsSelected = ReferenceEquals(provider, ViewModel.SelectedProvider) };
            card.Clicked += (_, _) =>
            {
                if (ViewModel.SelectProviderCommand.CanExecute(provider)) ViewModel.SelectProviderCommand.Execute(provider);
            };
            _cards.Add(card);
            host.AddArrangedSubview(card);
        }
    }

    private void UpdateSelection(IProviderDetail? selected)
    {
        foreach (var card in _cards) card.IsSelected = card.Provider is not null && ReferenceEquals(card.Provider, selected);
    }

    private static UIStackView Column(double spacing, params UIView[] views)
    {
        var stack = WinoLayout.VStack(spacing, views);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        foreach (var refresh in _catalogRefresh) refresh();
        // The palette is loaded by OnNavigatedTo and never raises a change of its own.
        _colors.Colors = ViewModel.AvailableColors.Select(color => color.Hex).ToList();
        _colors.SelectedHex = ViewModel.SelectedColor?.Hex;
        return Task.CompletedTask;
    }
}
