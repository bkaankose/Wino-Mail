using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Onboarding;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Provider selection wizard (Windows ProviderSelectionPage): featured provider tiles and an
/// "IMAP / SMTP" row that opens the searchable catalog of every known provider, then the account
/// identity, then the capabilities. Continue follows the shared ViewModel, which routes OAuth to
/// account setup, catalog providers to their credentials page and custom servers to the server page.
/// </summary>
public sealed class ProviderSelectionPageViewController(ProviderSelectionPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ProviderSelectionPageViewModel>(viewModel, dispatcher, logger)
{
    private const int ColumnCount = 2;

    private readonly List<ProviderCard> _cards = new();
    private readonly NSStackView _featuredTiles = WinoLayout.VStack(10);
    private readonly NSStackView _customTiles = WinoLayout.VStack(10);
    private readonly NSStackView _catalogTiles = WinoLayout.VStack(10);
    private FormBinder<ProviderSelectionPageViewModel> _form = null!;
    // The catalog list and its count derive from the search text, which OnNavigatedTo may leave
    // unchanged; these refresh once the providers are loaded.
    private readonly List<Action> _catalogRefresh = new();
    private readonly List<NSView> _keyViews = new();
    private readonly AccountColorPicker _colors = new();
    private readonly AccountColorButton _colorButton = new();
    private AccountCapabilityPicker? _capabilityPicker;

    public override void LoadView()
    {
        var vm = ViewModel;
        _form = new FormBinder<ProviderSelectionPageViewModel>(vm, Bindings, Dispatcher, ReportError);
        var title = NSTextField.CreateLabel(string.Empty);
        BindText(title, nameof(ViewModel.PageTitle), vm => vm.PageTitle);
        var subtitle = NSTextField.CreateLabel(string.Empty);
        BindText(subtitle, nameof(ViewModel.PageSubtitle), vm => vm.PageSubtitle);
        var stepIndicator = new WizardStepIndicator(ProviderSelectionPageViewModel.TotalStepCount);
        _form.Bind(nameof(vm.StepProgressText), s => (s.CurrentStepNumber, s.StepProgressText), step => stepIndicator.Set(step.CurrentStepNumber, step.StepProgressText));

        var continueButton = CommandButton(Translator.Buttons_Continue, ViewModel.ContinueCommand);
        _form.Bind(nameof(vm.ContinueButtonText), s => s.ContinueButtonText, text => continueButton.Title = text);
        var backButton = CommandButton(Translator.Buttons_Back, ViewModel.GoBackCommand);
        _form.Visible(backButton, nameof(vm.CanGoBack), s => s.CanGoBack);

        var providerStep = BuildProviderStep();
        _form.Visible(providerStep, nameof(vm.IsProviderStepVisible), s => s.IsProviderStepVisible);

        var summary = BuildAccountSummary();
        var identity = BuildIdentityStep();
        _form.Visible(identity, nameof(vm.IsIdentityStepVisible), s => s.IsIdentityStepVisible);
        var capabilities = BuildCapabilityStep();
        _form.Visible(capabilities, nameof(vm.IsCapabilityStepVisible), s => s.IsCapabilityStepVisible);

        var content = WinoLayout.VStack(20, providerStep, summary, identity, capabilities);
        foreach (var section in content.ArrangedSubviews) section.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
        View = new ProviderSelectionPage(title, subtitle, stepIndicator, content, backButton, continueButton);
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        OnboardingPageView.ChainKeyViews(_keyViews);
    }

    /// <summary>The chosen provider with the account colour button, on every step after the first.</summary>
    private NSView BuildAccountSummary()
    {
        var vm = ViewModel;
        var panel = new AccountSummaryPanel(_colorButton);
        _form.Visible(panel, nameof(vm.IsAccountSummaryVisible), s => s.IsAccountSummaryVisible);
        _form.Bind(nameof(vm.SelectedProvider), s => s.SelectedProvider, provider =>
        {
            panel.SetProvider(provider);
            panel.Name = vm.SelectedProviderName;
            _capabilityPicker?.QueueRefresh();
        });
        _form.Bind(nameof(vm.SelectedProviderSummaryDetail), s => s.SelectedProviderSummaryDetail, detail => panel.Detail = detail);
        _form.Bind(nameof(vm.SelectedColor), s => s.SelectedColor?.Hex, hex =>
        {
            panel.ColorHex = hex;
            _colorButton.SelectedHex = hex;
            _colors.SelectedHex = hex;
        });
        EventHandler<string> selected = (_, hex) => SelectColor(hex);
        EventHandler cleared = (_, _) => { if (vm.ClearColorCommand.CanExecute(null)) vm.ClearColorCommand.Execute(null); };
        _colorButton.ColorSelected += selected;
        _colorButton.Cleared += cleared;
        _colors.ColorSelected += selected;
        _colors.Cleared += cleared;
        Bindings.Own(new ActionDisposable(() =>
        {
            _colorButton.ColorSelected -= selected;
            _colorButton.Cleared -= cleared;
            _colors.ColorSelected -= selected;
            _colors.Cleared -= cleared;
        }));
        return panel;
    }

    private void SelectColor(string hex)
    {
        var color = ViewModel.AvailableColors.FirstOrDefault(item => string.Equals(item.Hex, hex, StringComparison.OrdinalIgnoreCase));
        if (color is not null) ViewModel.SelectedColor = color;
    }

    /// <summary>
    /// Account name and colour on one card (Windows IdentityStepPanel). The page title already
    /// names the step, so the card has no heading of its own; labels sit above full-width fields.
    /// </summary>
    private NSView BuildIdentityStep()
    {
        var vm = ViewModel;
        var name = _form.Text(nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v,
            Translator.ProviderSelection_AccountNamePlaceholder, Translator.ProviderSelection_AccountNameHeader);
        _keyViews.Add(name);
        var nameHeader = WinoStyle.Label(Translator.ProviderSelection_AccountNameHeader, WinoStyle.Body);
        var nameHelp = WinoStyle.Label(Translator.ProviderSelection_AccountNameDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        var colorHeader = WinoStyle.Label(Translator.AccountDetailsPage_ColorPicker_Title, WinoStyle.Body);
        var colorHelp = WinoStyle.Label(Translator.AccountDetailsPage_ColorPicker_Description, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
        var body = WinoLayout.VStack(6, nameHeader, name, nameHelp, separator, colorHeader, colorHelp, _colors);
        body.SetCustomSpacing((nfloat)WinoStyle.Space4, nameHelp);
        body.SetCustomSpacing((nfloat)WinoStyle.Space4, separator);
        body.SetCustomSpacing((nfloat)WinoStyle.Space2, colorHelp);
        foreach (var view in new NSView[] { name, nameHelp, separator, colorHelp, _colors })
            view.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        return ProviderSelectionPage.Card(body, WinoStyle.Space4);
    }

    /// <summary>
    /// What the account is used for (Windows capability step): the shared capability picker with
    /// the download range and its Everything warning under Mail, then the step's hints.
    /// </summary>
    private NSView BuildCapabilityStep()
    {
        var vm = ViewModel;
        var heading = ProviderSelectionPage.Heading(Translator.ProviderSelection_CapabilitySectionTitle, Translator.ProviderSelection_CapabilityIntroDescription);

        var range = _form.PopUp(vm.InitialSynchronizationRanges.Select(option => option.DisplayText), nameof(vm.SelectedInitialSynchronizationRange),
            s => s.InitialSynchronizationRanges.IndexOf(s.SelectedInitialSynchronizationRange),
            (s, index) => s.SelectedInitialSynchronizationRange = s.InitialSynchronizationRanges[index],
            Translator.AccountCreation_InitialSynchronization_Title);
        range.WidthAnchor.ConstraintGreaterThanOrEqualTo(160).Active = true;
        _form.Bind(nameof(vm.IsMailSynchronizationRangeVisible), s => s.IsMailSynchronizationRangeVisible, enabled => range.Enabled = enabled);
        var rangeText = WinoLayout.VStack(2, WinoStyle.Label(Translator.ProviderSelection_MailRangeHeader, WinoStyle.Body),
            WinoStyle.Label(Translator.ProviderSelection_MailRangeDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0));
        rangeText.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        var rangeRow = WinoLayout.HStack(WinoStyle.Space4, rangeText, range);
        var everythingWarning = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.GeneralTitle_Warning, Translator.AccountCreation_InitialSynchronization_EverythingWarning);
        _form.Visible(everythingWarning, nameof(vm.IsInitialSynchronizationWarningVisible), s => s.IsInitialSynchronizationWarningVisible);
        var mailOptions = WinoLayout.VStack(WinoStyle.Space3, rangeRow, everythingWarning);
        rangeRow.WidthAnchor.ConstraintEqualTo(mailOptions.WidthAnchor).Active = true;
        everythingWarning.WidthAnchor.ConstraintEqualTo(mailOptions.WidthAnchor).Active = true;

        _capabilityPicker = new AccountCapabilityPicker(vm.Capabilities, () => vm.MailProviderModeLabel, mailOptions, Bindings, Dispatcher, ReportError);
        _form.Bind(nameof(vm.MailProviderModeLabel), s => s.MailProviderModeLabel, _ => _capabilityPicker.QueueRefresh());
        _keyViews.Add(range);

        var calendarOnlyHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ProviderSelection_CalendarOnlyServerHint);
        _form.Visible(calendarOnlyHint, nameof(vm.IsCalendarOnlyServerHintVisible), s => s.IsCalendarOnlyServerHintVisible);
        var localOnlyHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ProviderSelection_LocalOnlyHint_Device);
        _form.Visible(localOnlyHint, nameof(vm.IsLocalOnlyHintVisible), s => s.IsLocalOnlyHintVisible);
        var cardDavHint = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, vm.DavContactAvailabilityMessage);
        _form.Visible(cardDavHint, nameof(vm.IsCardDavDiscoveryHintVisible), s => s.IsCardDavDiscoveryHintVisible);
        var missing = WinoStyle.Label(Translator.ProviderSelection_CapabilityValidationMessage, WinoStyle.Caption, WinoStyle.Critical, 0);
        _form.Visible(missing, nameof(vm.IsCapabilitySelectionMissing), s => s.IsCapabilitySelectionMissing);

        var step = WinoLayout.VStack(WinoStyle.Space3, heading, _capabilityPicker, calendarOnlyHint, localOnlyHint, cardDavHint, missing);
        step.SetCustomSpacing((nfloat)WinoStyle.Space4, heading);
        step.SetCustomSpacing((nfloat)20, _capabilityPicker);
        foreach (var view in step.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        return step;
    }

    /// <summary>Featured tiles with the IMAP / SMTP row, or the searchable catalog in their place.</summary>
    private NSView BuildProviderStep()
    {
        var vm = ViewModel;

        var more = new ProviderCard(WinoIconGlyph.ProviderCatalog, Translator.ProviderSelection_MoreProviders_Title,
            Translator.ProviderSelection_MoreProviders_Description, showsArrow: true);
        EventHandler showCatalog = (_, _) => vm.ShowCatalogCommand.Execute(null);
        more.Activated += showCatalog;
        Bindings.Own(new ActionDisposable(() => more.Activated -= showCatalog));
        var featured = ProviderSelectionPage.Card(Column(WinoStyle.Space3,
            ProviderSelectionPage.Heading(Translator.ProviderSelection_ProviderSectionTitle, Translator.ProviderSelection_ProviderSectionDescription),
            _featuredTiles, more));
        _form.Visible(featured, nameof(vm.IsFeaturedListVisible), s => s.IsFeaturedListVisible);

        var search = new NSSearchField { PlaceholderString = Translator.ProviderSelection_Catalog_SearchPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        search.AccessibilityLabel = Translator.ProviderSelection_Catalog_SearchPlaceholder;
        search.SendsSearchStringImmediately = true;
        search.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var searchBinding = Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, string>(vm, nameof(vm.CatalogSearchText),
            s => s.CatalogSearchText ?? string.Empty, value => { if (search.StringValue != value) search.StringValue = value; }, Dispatcher, ReportError,
            (s, value) => s.CatalogSearchText = value));
        EventHandler searched = (_, _) => searchBinding.UpdateSource(search.StringValue);
        search.Changed += searched;
        Bindings.Own(new ActionDisposable(() => search.Changed -= searched));
        var count = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        count.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        _catalogRefresh.Add(_form.Bind(nameof(vm.CatalogResultCountText), s => s.CatalogResultCountText, text => count.StringValue = text ?? string.Empty).Refresh);
        var searchRow = WinoLayout.HStack(WinoStyle.Space3, search, count);
        var empty = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _form.Label(empty, nameof(vm.CatalogNoResultsText), s => s.CatalogNoResultsText);
        _form.Visible(empty, nameof(vm.IsCatalogEmpty), s => s.IsCatalogEmpty);
        var catalog = ProviderSelectionPage.Card(Column(WinoStyle.Space3,
            ProviderSelectionPage.Heading(Translator.ProviderSelection_Catalog_SectionTitle, Translator.ProviderSelection_Catalog_SectionDescription),
            _customTiles, ProviderSelectionPage.Divider(Translator.ProviderSelection_Catalog_KnownProvidersHeader), searchRow, _catalogTiles, empty));
        _form.Visible(catalog, nameof(vm.IsCatalogVisible), s => s.IsCatalogVisible);

        _form.Bind(nameof(vm.FeaturedProviders), s => s.FeaturedProviders, providers => Fill(_featuredTiles, providers));
        _form.Bind(nameof(vm.CustomServerProviders), s => s.CustomServerProviders, providers => Fill(_customTiles, providers, columns: 1));
        _catalogRefresh.Add(_form.Bind(nameof(vm.FilteredCatalogProviders), s => s.FilteredCatalogProviders, providers => Fill(_catalogTiles, providers)).Refresh);
        _form.Bind(nameof(vm.SelectedProvider), s => s.SelectedProvider, UpdateSelection);
        return Column(WinoStyle.Space4, featured, catalog);
    }

    /// <summary>Replaces the tiles of one list, two per row.</summary>
    private void Fill(NSStackView host, IReadOnlyList<IProviderDetail>? providers, int columns = ColumnCount)
    {
        foreach (var view in host.ArrangedSubviews)
        {
            foreach (var card in view.Subviews.OfType<ProviderCard>()) _cards.Remove(card);
            host.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        providers ??= [];
        for (int index = 0; index < providers.Count; index += columns)
        {
            var row = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
                Distribution = NSStackViewDistribution.FillEqually,
                Spacing = 10,
                Alignment = NSLayoutAttribute.Height,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            foreach (var provider in providers.Skip(index).Take(columns))
            {
                var card = new ProviderCard(provider) { IsSelected = ReferenceEquals(provider, ViewModel.SelectedProvider) };
                card.Activated += (_, _) =>
                {
                    if (ViewModel.SelectProviderCommand.CanExecute(provider)) ViewModel.SelectProviderCommand.Execute(provider);
                };
                _cards.Add(card);
                row.AddArrangedSubview(card);
            }
            // A lone last tile keeps the column width.
            if (row.ArrangedSubviews.Length < columns) row.AddArrangedSubview(new NSView { TranslatesAutoresizingMaskIntoConstraints = false });
            host.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(host.WidthAnchor).Active = true;
        }
    }

    private void UpdateSelection(IProviderDetail? selected)
    {
        foreach (var card in _cards) card.IsSelected = card.Provider is not null && ReferenceEquals(card.Provider, selected);
    }

    private static NSStackView Column(double spacing, params NSView[] views)
    {
        var stack = WinoLayout.VStack(spacing, views);
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in views) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        foreach (var refresh in _catalogRefresh) refresh();
        // The palette is loaded by OnNavigatedTo and never raises a change of its own.
        var palette = ViewModel.AvailableColors.Select(color => color.Hex).ToList();
        _colors.Colors = palette;
        _colorButton.Colors = palette;
        _colors.SelectedHex = _colorButton.SelectedHex = ViewModel.SelectedColor?.Hex;
#if DEBUG
        RegisterDebugCommands();
#endif
        return Task.CompletedTask;
    }

#if DEBUG
    /// <summary>
    /// Debug bridge: <c>provider NAME</c> selects a provider by name or catalog id (opening the catalog
    /// when it has no tile), <c>provider-name NAME</c> (the account name), <c>provider-catalog</c>,
    /// <c>provider-continue</c>, <c>provider-back</c>, <c>provider-color HEX|first|none</c>,
    /// <c>provider-capability mail|calendar|contacts|tasks provider|local|off</c> and
    /// <c>provider-range INDEX|everything</c>.
    /// Only selection and navigation; no credential is ever filled.
    /// </summary>
    private void RegisterDebugCommands()
    {
        MacDebugBridge.Register("provider", args => Dispatch(() =>
        {
            var query = string.Join(' ', args);
            var provider = ViewModel.Providers.FirstOrDefault(item => string.Equals(item.Name, query, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.Providers.FirstOrDefault(item => item.SpecialImapProvider != SpecialImapProvider.None
                    && string.Equals(item.SpecialImapProvider.ToString(), query, StringComparison.OrdinalIgnoreCase))
                ?? ViewModel.Providers.FirstOrDefault(item => string.Equals(item.Type.ToString(), query, StringComparison.OrdinalIgnoreCase)
                    && item.SpecialImapProvider == SpecialImapProvider.None)
                ?? ViewModel.Providers.FirstOrDefault(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return "no provider " + query + "; known: " + string.Join(", ", ViewModel.Providers.Select(item => item.Name));
            if (!provider.IsFeatured && !ViewModel.IsCatalogVisible) ViewModel.ShowCatalogCommand.Execute(null);
            if (provider.IsFeatured && ViewModel.IsCatalogVisible) ViewModel.ShowFeaturedCommand.Execute(null);
            ViewModel.SelectProviderCommand.Execute(provider);
            return "selected " + provider.Name;
        }));
        // The account's display name only (not a credential), so the identity step can continue.
        MacDebugBridge.Register("provider-name", args => Dispatch(() => { ViewModel.AccountName = string.Join(' ', args); return "ok"; }));
        MacDebugBridge.Register("provider-catalog", _ => Dispatch(() => { ViewModel.ShowCatalogCommand.Execute(null); return "ok"; }));
        MacDebugBridge.Register("provider-continue", async _ =>
        {
            if (!ViewModel.ContinueCommand.CanExecute(null)) return "cannot continue";
            await ViewModel.ContinueCommand.ExecuteAsync(null);
            return "ok " + ViewModel.CurrentStep;
        });
        MacDebugBridge.Register("provider-back", _ => Dispatch(() => { ViewModel.GoBackCommand.Execute(null); return "ok"; }));
        MacDebugBridge.Register("provider-color", args => Dispatch(() =>
        {
            var value = args.Length > 0 ? args[0] : "first";
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) { ViewModel.ClearColorCommand.Execute(null); return "cleared"; }
            var color = value.Equals("first", StringComparison.OrdinalIgnoreCase)
                ? ViewModel.AvailableColors.FirstOrDefault()
                : ViewModel.AvailableColors.FirstOrDefault(item => string.Equals(item.Hex.TrimStart('#'), value.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
            if (color is null) return "no colour " + value + "; known: " + string.Join(", ", ViewModel.AvailableColors.Select(item => item.Hex));
            ViewModel.SelectedColor = color;
            return "selected " + color.Hex;
        }));
        MacDebugBridge.Register("provider-capability", args => Dispatch(() =>
        {
            if (args.Length < 2) return "usage: provider-capability mail|calendar|contacts|tasks provider|local|off";
            ViewModel.ChooseCapabilityCommand.Execute(args[0] + ":" + args[1]);
            return $"mail={ViewModel.MailMode} calendar={ViewModel.CalendarMode} contacts={ViewModel.ContactMode} tasks={ViewModel.TaskMode}";
        }));
        MacDebugBridge.Register("provider-range", args => Dispatch(() =>
        {
            var ranges = ViewModel.InitialSynchronizationRanges;
            var query = args.Length > 0 ? args[0] : "everything";
            var option = query.Equals("everything", StringComparison.OrdinalIgnoreCase)
                ? ranges.FirstOrDefault(item => item.IsEverything)
                : int.TryParse(query, out var index) && index >= 0 && index < ranges.Count ? ranges[index] : null;
            if (option is null) return "no range " + query;
            ViewModel.SelectedInitialSynchronizationRange = option;
            return "selected " + option.DisplayText;
        }));
    }

    private async Task<string> Dispatch(Func<string> action)
    {
        string result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = action());
        return result;
    }
#endif
}
