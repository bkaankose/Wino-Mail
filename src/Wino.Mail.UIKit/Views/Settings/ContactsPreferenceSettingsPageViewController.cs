using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// People preferences: new contact destination, name display, sort order and suggested recipients (Windows
/// ContactsPreferenceSettingsPage). The pop-ups sit under the card text on phone width; the specific address book
/// pop-up follows the behaviour pop-up when the behaviour asks for one.
/// </summary>
public sealed class ContactsPreferenceSettingsPageViewController(ContactsPreferenceSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<ContactsPreferenceSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var behavior = Bind.PopUp<ContactsPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, s => s.DestinationBehaviors.ToList(), o => o.DisplayText,
            nameof(vm.SelectedDestinationBehavior), s => s.SelectedDestinationBehavior, (s, v) => s.SelectedDestinationBehavior = v);
        var destination = Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactDestinationPreferenceOption>(vm, s => s.Destinations.ToList(), o => o.DisplayText,
            nameof(vm.SelectedDestination), s => s.SelectedDestination, (s, v) => s.SelectedDestination = v, itemsCollection: vm.Destinations);
        Bind.Visible(destination, vm, nameof(vm.ShouldShowSpecificDestination), s => s.ShouldShowSpecificDestination);
        var newContacts = WinoLayout.VStack(WinoStyle.Space2, behavior, destination);
        newContacts.Alignment = UIStackViewAlignment.Fill;

        AddGroup(null,
            StackedCard(Translator.PeopleSettings_NewContacts_Header, Translator.PeopleSettings_NewContacts_Description, WinoIconGlyph.Add, newContacts),
            StackedCard(Translator.PeopleSettings_NameDisplay_Header, Translator.PeopleSettings_NameDisplay_Description, WinoIconGlyph.Person,
                Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactNameDisplayOption>(vm, s => s.NameDisplayOptions.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedNameDisplay), s => s.SelectedNameDisplay, (s, v) => s.SelectedNameDisplay = v)),
            StackedCard(Translator.PeopleSettings_Sort_Header, Translator.PeopleSettings_Sort_Description, WinoIconGlyph.ArrowSort,
                Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactSortOption>(vm, s => s.SortOptions.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedSort), s => s.SelectedSort, (s, v) => s.SelectedSort = v)),
            StackedCard(Translator.PeopleSettings_SuggestedRecipients_Header, Translator.PeopleSettings_SuggestedRecipients_Description, WinoIconGlyph.History,
                SettingsCardLayout.Leading(Bind.Button(Translator.PeopleSettings_SuggestedRecipients_Clear, vm.ClearSuggestedRecipientsCommand))));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter);
}
