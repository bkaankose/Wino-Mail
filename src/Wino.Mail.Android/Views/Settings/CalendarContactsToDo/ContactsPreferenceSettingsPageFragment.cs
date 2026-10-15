using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Android.Views.Settings.CalendarContactsToDo;

/// <summary>
/// People preferences: new contact destination, name display, sort order and suggested recipients (Windows
/// ContactsPreferenceSettingsPage, iOS ContactsPreferenceSettingsPageViewController). The pop-ups sit under the card
/// text on phone width; the specific address book pop-up follows the behaviour pop-up when the behaviour asks for one.
/// </summary>
public sealed class ContactsPreferenceSettingsPageFragment : SettingsPageFragment<ContactsPreferenceSettingsPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var behavior = Bind.PopUp<ContactsPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, s => s.DestinationBehaviors.ToList(), o => o.DisplayText,
            nameof(vm.SelectedDestinationBehavior), s => s.SelectedDestinationBehavior, (s, v) => s.SelectedDestinationBehavior = v);
        var destination = Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactDestinationPreferenceOption>(vm, s => s.Destinations.ToList(), o => o.DisplayText,
            nameof(vm.SelectedDestination), s => s.SelectedDestination, (s, v) => s.SelectedDestination = v, itemsCollection: vm.Destinations);
        Bind.Visible(destination, vm, nameof(vm.ShouldShowSpecificDestination), s => s.ShouldShowSpecificDestination);

        AddGroup(null,
            StackedCard(Translator.PeopleSettings_NewContacts_Header, Translator.PeopleSettings_NewContacts_Description, WinoIconGlyph.Add, Stack(Resource.Dimension.wino_space_s, behavior, destination)),
            StackedCard(Translator.PeopleSettings_NameDisplay_Header, Translator.PeopleSettings_NameDisplay_Description, WinoIconGlyph.Person,
                Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactNameDisplayOption>(vm, s => s.NameDisplayOptions.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedNameDisplay), s => s.SelectedNameDisplay, (s, v) => s.SelectedNameDisplay = v)),
            StackedCard(Translator.PeopleSettings_Sort_Header, Translator.PeopleSettings_Sort_Description, WinoIconGlyph.ArrowSort,
                Bind.PopUp<ContactsPreferenceSettingsPageViewModel, ContactSortOption>(vm, s => s.SortOptions.ToList(), o => o.DisplayText,
                    nameof(vm.SelectedSort), s => s.SelectedSort, (s, v) => s.SelectedSort = v)),
            StackedCard(Translator.PeopleSettings_SuggestedRecipients_Header, Translator.PeopleSettings_SuggestedRecipients_Description, WinoIconGlyph.History,
                Row(Bind.Button(Translator.PeopleSettings_SuggestedRecipients_Clear, vm.ClearSuggestedRecipientsCommand))));
    }
}
