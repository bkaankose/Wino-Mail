using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class ProviderSelectionPageViewController(ProviderSelectionPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ProviderSelectionPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSStackView _providers = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical);
    private readonly NSStackView _identity = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical);
    private readonly NSStackView _capabilities = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical);
    private readonly NSPopUpButton _colors = new();
    private readonly NSPopUpButton _range = new();

    public override void LoadView()
    {
        var title = NSTextField.CreateLabel(string.Empty);
        title.Font = NSFont.BoldSystemFontOfSize(24);
        BindText(title, nameof(ViewModel.PageTitle), vm => vm.PageTitle);
        var subtitle = NSTextField.CreateLabel(string.Empty);
        BindText(subtitle, nameof(ViewModel.PageSubtitle), vm => vm.PageSubtitle);
        var name = new NSTextField { PlaceholderString = Translator.ProviderSelection_AccountNamePlaceholder };
        var nameBinding = Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, string>(ViewModel,
            nameof(ViewModel.AccountName), vm => vm.AccountName, value => name.StringValue = value ?? string.Empty,
            Dispatcher, ReportError, (vm, value) => vm.AccountName = value));
        EventHandler nameChanged = (_, _) => nameBinding.UpdateSource(name.StringValue);
        name.Changed += nameChanged;
        Bindings.Own(new ActionDisposable(() => name.Changed -= nameChanged));
        _identity.AddArrangedSubview(NSTextField.CreateLabel(Translator.ProviderSelection_AccountNameHeader));
        _identity.AddArrangedSubview(name);
        _identity.AddArrangedSubview(NSTextField.CreateLabel(Translator.ProviderSelection_AccountColorHeader));
        _identity.AddArrangedSubview(_colors);
        _identity.AddArrangedSubview(CommandButton(Translator.ProviderSelection_ClearColor, ViewModel.ClearColorCommand));
        _identity.AddArrangedSubview(NSTextField.CreateLabel(Translator.ProviderSelection_MailRangeHeader));
        _identity.AddArrangedSubview(_range);
        AddCapability(Translator.ProviderSelection_MailStepTitle, "Mail", () => ViewModel.MailMode);
        AddCapability(Translator.ProviderSelection_CalendarStepTitle, "Calendar", () => ViewModel.CalendarMode);
        AddCapability(Translator.ProviderSelection_ContactsStepTitle, "Contacts", () => ViewModel.ContactMode);
        AddCapability(Translator.ProviderSelection_TasksStepTitle, "Tasks", () => ViewModel.TaskMode);
        var continueButton = CommandButton(Translator.Buttons_Continue, ViewModel.ContinueCommand);
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, string>(ViewModel, nameof(ViewModel.ContinueButtonText),
            vm => vm.ContinueButtonText, value => continueButton.Title = value, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, bool>(ViewModel, nameof(ViewModel.IsProviderStepVisible), vm => vm.IsProviderStepVisible, value => _providers.Hidden = !value, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, bool>(ViewModel, nameof(ViewModel.IsIdentityStepVisible), vm => vm.IsIdentityStepVisible, value => _identity.Hidden = !value, Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, bool>(ViewModel, nameof(ViewModel.IsCapabilityStepVisible), vm => vm.IsCapabilityStepVisible, value => _capabilities.Hidden = !value, Dispatcher, ReportError));
        View = new ProviderSelectionPage(title, subtitle, _providers, _identity, _capabilities,
            Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, CommandButton(Translator.Buttons_Back, ViewModel.GoBackCommand), continueButton));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        foreach (var provider in ViewModel.Providers.Where(item => item.Type is MailProviderType.Outlook or MailProviderType.Gmail))
            _providers.AddArrangedSubview(CommandButton(provider.Name, ViewModel.SelectProviderCommand, () => provider));
        _colors.AddItems(ViewModel.AvailableColors.Select(color => color.Hex).ToArray());
        _range.AddItems(ViewModel.InitialSynchronizationRanges.Select(range => range.DisplayText).ToArray());
        _colors.SelectItem(Math.Max(0, ViewModel.AvailableColors.IndexOf(ViewModel.SelectedColor)));
        _range.SelectItem(Math.Max(0, ViewModel.InitialSynchronizationRanges.IndexOf(ViewModel.SelectedInitialSynchronizationRange)));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, int>(ViewModel, nameof(ViewModel.SelectedColor),
            vm => vm.AvailableColors.IndexOf(vm.SelectedColor), index => _colors.SelectItem(index), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, int>(ViewModel, nameof(ViewModel.SelectedInitialSynchronizationRange),
            vm => vm.InitialSynchronizationRanges.IndexOf(vm.SelectedInitialSynchronizationRange), index => _range.SelectItem(index), Dispatcher, ReportError));
        EventHandler colorsChanged = (_, _) => { if (_colors.IndexOfSelectedItem >= 0) ViewModel.SelectedColor = ViewModel.AvailableColors[(int)_colors.IndexOfSelectedItem]; };
        EventHandler rangeChanged = (_, _) => { if (_range.IndexOfSelectedItem >= 0) ViewModel.SelectedInitialSynchronizationRange = ViewModel.InitialSynchronizationRanges[(int)_range.IndexOfSelectedItem]; };
        _colors.Activated += colorsChanged;
        _range.Activated += rangeChanged;
        Bindings.Own(new ActionDisposable(() => { _colors.Activated -= colorsChanged; _range.Activated -= rangeChanged; }));
        return Task.CompletedTask;
    }

    private void AddCapability(string title, string capability, Func<AccountCapabilityMode> read)
    {
        var picker = new NSPopUpButton();
        var modes = capability == "Mail"
            ? new[] { AccountCapabilityMode.Off, AccountCapabilityMode.Provider }
            : new[] { AccountCapabilityMode.Off, AccountCapabilityMode.Local, AccountCapabilityMode.Provider };
        picker.AddItems(modes.Select(mode => mode switch
        {
            AccountCapabilityMode.Off => Translator.ProviderSelection_Choice_Off,
            AccountCapabilityMode.Local => Translator.ProviderSelection_Choice_Local,
            _ => Translator.ProviderSelection_SourceTitle
        }).ToArray());
        picker.SelectItem(Array.IndexOf(modes, read()));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, string>(ViewModel, nameof(ViewModel.SelectedProviderName),
            vm => vm.SelectedProviderName, name => picker.ItemAtIndex(Array.IndexOf(modes, AccountCapabilityMode.Provider)).Title = name,
            Dispatcher, ReportError));
        EventHandler changed = (_, _) => ViewModel.ChooseCapabilityCommand.Execute($"{capability}:{modes[(int)picker.IndexOfSelectedItem]}");
        picker.Activated += changed;
        Bindings.Own(new ActionDisposable(() => picker.Activated -= changed));
        Bindings.Own(new PropertyBinding<ProviderSelectionPageViewModel, AccountCapabilityMode>(ViewModel,
            capability == "Contacts" ? nameof(ViewModel.ContactMode) : capability == "Tasks" ? nameof(ViewModel.TaskMode) : capability + "Mode",
            _ => read(), value => picker.SelectItem(Array.IndexOf(modes, value)), Dispatcher, ReportError));
        _capabilities.AddArrangedSubview(Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, NSTextField.CreateLabel(title), picker));
    }
}
