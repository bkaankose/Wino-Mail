using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>People preferences (Windows ContactsPreferenceSettingsPage): new contacts, name display, sort, suggested recipients.</summary>
internal sealed class ContactsPreferenceSettingsPage(ContactsPreferenceSettingsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<ContactsPreferenceSettingsPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var newContacts = WinoSettingsRows.Action(Translator.PeopleSettings_NewContacts_Header, Translator.PeopleSettings_NewContacts_Description, WinoIconGlyph.Add);
        WinoSettingsRows.Suffix(newContacts, Settings.ChoiceSuffix<ContactsPreferenceSettingsPageViewModel, DestinationBehaviorOption>(vm, Translator.PeopleSettings_NewContacts_Header,
            static s => s.DestinationBehaviors, static o => o.DisplayText,
            nameof(vm.SelectedDestinationBehavior), static s => s.SelectedDestinationBehavior, static (s, v) => s.SelectedDestinationBehavior = v));
        var destination = Settings.Visible(Dependent(Settings.ChoiceSuffix<ContactsPreferenceSettingsPageViewModel, ContactDestinationPreferenceOption>(vm, Translator.PeopleSettings_NewContacts_Header,
            static s => s.Destinations, static o => o.DisplayText,
            nameof(vm.SelectedDestination), static s => s.SelectedDestination, static (s, v) => s.SelectedDestination = v, itemsCollection: vm.Destinations)),
            vm, nameof(vm.ShouldShowSpecificDestination), static s => s.ShouldShowSpecificDestination);

        var clear = WinoSettingsRows.Action(Translator.PeopleSettings_SuggestedRecipients_Header, Translator.PeopleSettings_SuggestedRecipients_Description, WinoIconGlyph.History);
        WinoSettingsRows.Suffix(clear, Settings.Button(Translator.PeopleSettings_SuggestedRecipients_Clear, vm.ClearSuggestedRecipientsCommand));

        AddGroup(null,
            newContacts,
            destination,
            Settings.Choice<ContactsPreferenceSettingsPageViewModel, ContactNameDisplayOption>(vm, Translator.PeopleSettings_NameDisplay_Header, Translator.PeopleSettings_NameDisplay_Description, WinoIconGlyph.Person,
                static s => s.NameDisplayOptions, static o => o.DisplayText,
                nameof(vm.SelectedNameDisplay), static s => s.SelectedNameDisplay, static (s, v) => s.SelectedNameDisplay = v),
            Settings.Choice<ContactsPreferenceSettingsPageViewModel, ContactSortOption>(vm, Translator.PeopleSettings_Sort_Header, Translator.PeopleSettings_Sort_Description, WinoIconGlyph.ArrowSort,
                static s => s.SortOptions, static o => o.DisplayText,
                nameof(vm.SelectedSort), static s => s.SelectedSort, static (s, v) => s.SelectedSort = v),
            clear);
    }

    // The destination list is wide; beside the behaviour drop-down it squeezes the title, so it gets the row below.
    private static Adw.PreferencesRow Dependent(Gtk.DropDown dropDown)
    {
        dropDown.SetHalign(Gtk.Align.End);
        dropDown.SetHexpand(true);
        return WinoSettingsRows.Custom(dropDown);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);
}
