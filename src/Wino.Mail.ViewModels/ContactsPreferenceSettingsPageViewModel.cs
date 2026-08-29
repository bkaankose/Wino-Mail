using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class ContactsPreferenceSettingsPageViewModel(
    IPreferencesService preferencesService,
    IContactQueryService contactService) : CoreBaseViewModel
{
    private bool _isLoaded;

    public ObservableCollection<DestinationBehaviorOption> DestinationBehaviors { get; } =
    [
        new(NewItemDestinationBehavior.AskEachTime, Translator.ProductivitySettings_AskEveryTime),
        new(NewItemDestinationBehavior.LastUsed, Translator.ProductivitySettings_LastUsed),
        new(NewItemDestinationBehavior.Specific, Translator.ProductivitySettings_SpecificDestination)
    ];

    public ObservableCollection<ContactNameDisplayOption> NameDisplayOptions { get; } =
    [
        new(ContactNameDisplayFormat.FirstNameFirst, Translator.PeopleSettings_NameDisplay_FirstNameFirst),
        new(ContactNameDisplayFormat.LastNameFirst, Translator.PeopleSettings_NameDisplay_LastNameFirst),
        new(ContactNameDisplayFormat.ProviderDisplayName, Translator.PeopleSettings_NameDisplay_Provider)
    ];

    public ObservableCollection<ContactSortOption> SortOptions { get; } =
    [
        new(ContactSortOrder.FirstName, Translator.PeopleSettings_Sort_FirstName),
        new(ContactSortOrder.LastName, Translator.PeopleSettings_Sort_LastName),
        new(ContactSortOrder.ProviderDisplayName, Translator.PeopleSettings_Sort_Provider)
    ];

    public ObservableCollection<ContactDestinationPreferenceOption> Destinations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowSpecificDestination))]
    public partial DestinationBehaviorOption SelectedDestinationBehavior { get; set; }

    [ObservableProperty]
    public partial ContactDestinationPreferenceOption SelectedDestination { get; set; }

    [ObservableProperty]
    public partial ContactNameDisplayOption SelectedNameDisplay { get; set; }

    [ObservableProperty]
    public partial ContactSortOption SelectedSort { get; set; }

    public bool ShouldShowSpecificDestination
        => SelectedDestinationBehavior?.Behavior == NewItemDestinationBehavior.Specific;

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isLoaded = false;

        var destinations = (await contactService.GetCreateDestinationsAsync().ConfigureAwait(false))
            .Where(destination => !destination.IsReadOnly)
            .Select(destination => new ContactDestinationPreferenceOption(destination))
            .ToList();

        await ExecuteUIThread(() =>
        {
            Destinations.Clear();
            foreach (var destination in destinations)
                Destinations.Add(destination);

            var selectedDestination = preferencesService.SpecificContactAddressBookId is { } destinationId
                ? Destinations.FirstOrDefault(item => item.Destination.AddressBookId == destinationId)
                : null;

            if (preferencesService.ContactCreationBehavior == NewItemDestinationBehavior.Specific && selectedDestination is null)
            {
                preferencesService.ContactCreationBehavior = NewItemDestinationBehavior.AskEachTime;
                preferencesService.SpecificContactAddressBookId = null;
            }

            SelectedDestinationBehavior = DestinationBehaviors.First(option => option.Behavior == preferencesService.ContactCreationBehavior);
            SelectedDestination = selectedDestination ?? Destinations.FirstOrDefault();
            SelectedNameDisplay = NameDisplayOptions.First(option => option.Format == preferencesService.ContactNameDisplayFormat);
            SelectedSort = SortOptions.First(option => option.Order == preferencesService.ContactSortOrder);
            _isLoaded = true;
        });
    }

    partial void OnSelectedDestinationBehaviorChanged(DestinationBehaviorOption value)
    {
        if (!_isLoaded || value is null)
            return;

        preferencesService.ContactCreationBehavior = value.Behavior;
        preferencesService.SpecificContactAddressBookId = value.Behavior == NewItemDestinationBehavior.Specific
            ? SelectedDestination?.Destination.AddressBookId
            : null;
    }

    partial void OnSelectedDestinationChanged(ContactDestinationPreferenceOption value)
    {
        if (_isLoaded && SelectedDestinationBehavior?.Behavior == NewItemDestinationBehavior.Specific)
            preferencesService.SpecificContactAddressBookId = value?.Destination.AddressBookId;
    }

    partial void OnSelectedNameDisplayChanged(ContactNameDisplayOption value)
    {
        if (_isLoaded && value is not null)
            preferencesService.ContactNameDisplayFormat = value.Format;
    }

    partial void OnSelectedSortChanged(ContactSortOption value)
    {
        if (_isLoaded && value is not null)
            preferencesService.ContactSortOrder = value.Order;
    }
}
