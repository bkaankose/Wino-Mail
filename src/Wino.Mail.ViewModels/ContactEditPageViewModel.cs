using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Requests.Contact;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class ContactEditPageViewModel : MailBaseViewModel, IConfirmBackNavigation
{
    private readonly IContactQueryService _contactService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INavigationService _navigationService;
    private readonly IMailDialogService _dialogService;
    private readonly IPreferencesService _preferencesService;
    private readonly IContactPictureFileService _pictureFileService;
    private AccountContact _original;
    private byte[] _photoBytes;
    private bool _deletePhoto;
    private string _previewPhotoPath;
    private bool _isSaveInProgress;
    private IReadOnlyList<Guid> _originalListIds = [];

    public ObservableCollection<ContactCreateDestination> Destinations { get; } = [];
    public ObservableCollection<ContactEmailAddress> EmailAddresses { get; } = [];
    public ObservableCollection<ContactPhoneNumber> PhoneNumbers { get; } = [];
    public ObservableCollection<ContactPostalAddress> PostalAddresses { get; } = [];
    public ObservableCollection<ContactImAddress> ImAddresses { get; } = [];
    public ObservableCollection<ContactRelation> Relations { get; } = [];

    /// <summary>Every local list, with <see cref="ContactListMembershipViewModel.IsMember"/> reflecting this contact.</summary>
    public ObservableCollection<ContactListMembershipViewModel> ListMemberships { get; } = [];

    public bool HasLists => ListMemberships.Count > 0;

    [ObservableProperty] public partial ContactCreateDestination SelectedDestination { get; set; }
    [ObservableProperty] public partial ContactEditorCategory SelectedCategory { get; set; }
    [ObservableProperty] public partial bool IsEditMode { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial bool IsSaving { get; set; }
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial string HonorificPrefix { get; set; }
    [ObservableProperty] public partial string GivenName { get; set; }
    [ObservableProperty] public partial string MiddleName { get; set; }
    [ObservableProperty] public partial string Surname { get; set; }
    [ObservableProperty] public partial string HonorificSuffix { get; set; }
    [ObservableProperty] public partial string Nickname { get; set; }
    [ObservableProperty] public partial string FileAs { get; set; }
    [ObservableProperty] public partial string CompanyName { get; set; }
    [ObservableProperty] public partial string Department { get; set; }
    [ObservableProperty] public partial string JobTitle { get; set; }
    [ObservableProperty] public partial string OfficeLocation { get; set; }
    [ObservableProperty] public partial string Profession { get; set; }
    [ObservableProperty] public partial int? BirthdayYear { get; set; }
    [ObservableProperty] public partial int? BirthdayMonth { get; set; }
    [ObservableProperty] public partial int? BirthdayDay { get; set; }
    [ObservableProperty] public partial string Website { get; set; }
    [ObservableProperty] public partial string Notes { get; set; }
    [ObservableProperty] public partial string ManagerName { get; set; }
    [ObservableProperty] public partial string AssistantName { get; set; }
    [ObservableProperty] public partial string SpouseName { get; set; }
    [ObservableProperty] public partial string SourceDescription { get; set; }
    [ObservableProperty] public partial bool IsFavorite { get; set; }

    public byte[] PreviewPhotoBytes => _photoBytes;
    public string PreviewPhotoPath => _previewPhotoPath;

    public string PageTitle => IsEditMode ? Translator.ContactEditDialog_Title : Translator.ContactEditDialog_AddTitle;
    public string PreviewDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
                return DisplayName.Trim();

            var structuredName = string.Join(" ", new[] { GivenName, Surname }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(structuredName))
                return structuredName;

            return !string.IsNullOrWhiteSpace(CompanyName) ? CompanyName.Trim() : PageTitle;
        }
    }

    public string PreviewSubtitle
    {
        get
        {
            var workDescription = string.Join(" · ", new[] { JobTitle, CompanyName }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(workDescription))
                return workDescription;

            return IsEditMode ? SourceDescription : SelectedDestination?.DisplayName;
        }
    }
    public double BirthdayYearValue { get => BirthdayYear ?? double.NaN; set { BirthdayYear = double.IsNaN(value) ? null : (int)value; IsDirty = true; } }
    public double BirthdayMonthValue { get => BirthdayMonth ?? double.NaN; set { BirthdayMonth = double.IsNaN(value) ? null : (int)value; IsDirty = true; } }
    public double BirthdayDayValue { get => BirthdayDay ?? double.NaN; set { BirthdayDay = double.IsNaN(value) ? null : (int)value; IsDirty = true; } }

    public ContactEditPageViewModel(IContactQueryService contactService, IWinoRequestDelegator requestDelegator,
        INavigationService navigationService, IMailDialogService dialogService, IContactPictureFileService pictureFileService,
        IPreferencesService preferencesService = null)
    {
        _contactService = contactService;
        _requestDelegator = requestDelegator;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _pictureFileService = pictureFileService;
        _preferencesService = preferencesService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isSaveInProgress = false;
        _original = null;
        _photoBytes = null;
        _previewPhotoPath = null;
        _deletePhoto = false;
        IsEditMode = false;
        SelectedCategory = ContactEditorCategory.ContactInformation;
        var parameter = parameters as ContactEditNavigationParameter ?? new();
        var destinations = await _contactService.GetCreateDestinationsAsync().ConfigureAwait(false);
        var original = parameter.ContactId is Guid contactId
            ? await _contactService.GetContactAsync(contactId).ConfigureAwait(false)
            : null;

        var lists = await _contactService.GetContactListsAsync().ConfigureAwait(false);
        List<Guid> memberListIds = original is null
            ? []
            : await _contactService.GetListIdsForContactAsync(original.Id).ConfigureAwait(false);
        _originalListIds = memberListIds.ToArray();

        if (parameter.ContactId is not null && original is null)
        {
            await ExecuteUIThread(() => _navigationService.GoBack());
            return;
        }

        await ExecuteUIThread(() =>
        {
            Destinations.Clear();
            foreach (var destination in destinations)
                Destinations.Add(destination);

            ListMemberships.Clear();
            foreach (var list in lists)
            {
                var membership = new ContactListMembershipViewModel(list, memberListIds.Contains(list.Id));
                membership.PropertyChanged += (_, _) => IsDirty = true;
                ListMemberships.Add(membership);
            }

            OnPropertyChanged(nameof(HasLists));

            if (original is not null)
            {
                _original = original;
                _previewPhotoPath = original.ContactPictureFileId is Guid pictureFileId
                    ? _pictureFileService.GetContactPicturePath(pictureFileId)
                    : null;
                IsEditMode = true;
                IsFavorite = original.IsFavorite;
                SelectedDestination = Destinations.FirstOrDefault(destination => destination.AddressBookId == original.AddressBookId);
                SourceDescription = SelectedDestination is null ? original.SourceKind.ToString() : $"{SelectedDestination.AccountName} · {SelectedDestination.AddressBookName}";
                Load(original);
            }
            else
            {
                var preferredDestinationId = _preferencesService?.ContactCreationBehavior switch
                {
                    NewItemDestinationBehavior.Specific => _preferencesService.SpecificContactAddressBookId,
                    NewItemDestinationBehavior.LastUsed => _preferencesService.LastUsedContactAddressBookId,
                    _ => null
                };
                SelectedDestination = Destinations.FirstOrDefault(destination => !destination.IsReadOnly && destination.AddressBookId == preferredDestinationId)
                    ?? Destinations.Where(destination => !destination.IsReadOnly).OrderByDescending(destination => destination.SourceKind != ContactSourceKind.Local)
                    .ThenByDescending(destination => destination.IsDefault).FirstOrDefault()
                    ?? Destinations.FirstOrDefault(destination => !destination.IsReadOnly);

                if (_preferencesService?.ContactCreationBehavior == NewItemDestinationBehavior.Specific &&
                    preferredDestinationId.HasValue && SelectedDestination?.AddressBookId != preferredDestinationId)
                {
                    _preferencesService.ContactCreationBehavior = NewItemDestinationBehavior.AskEachTime;
                    _preferencesService.SpecificContactAddressBookId = null;
                }

                if (parameter.ImportDraft is { } importDraft)
                {
                    Load(importDraft.Contact);
                    _photoBytes = importDraft.PhotoBytes;
                }
                else
                {
                    EmailAddresses.Add(new ContactEmailAddress { Id = Guid.NewGuid(), IsPrimary = true });
                    PostalAddresses.Add(new ContactPostalAddress { Id = Guid.NewGuid(), Kind = ContactPostalAddressKind.Home });
                    PostalAddresses.Add(new ContactPostalAddress { Id = Guid.NewGuid(), Kind = ContactPostalAddressKind.Business });
                    PostalAddresses.Add(new ContactPostalAddress { Id = Guid.NewGuid(), Kind = ContactPostalAddressKind.Other });
                }
            }

            IsDirty = parameter.ImportDraft != null;
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PreviewDisplayName));
            OnPropertyChanged(nameof(PreviewSubtitle));
            OnPropertyChanged(nameof(PreviewPhotoBytes));
            OnPropertyChanged(nameof(PreviewPhotoPath));
        });
    }

    private bool CanSave() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!IsEditMode && _preferencesService?.ContactCreationBehavior == NewItemDestinationBehavior.AskEachTime)
        {
            var pickedDestination = await _dialogService.ShowContactDestinationPickerDialogAsync(
                Destinations.Where(destination => !destination.IsReadOnly).ToList());
            if (pickedDestination is null)
                return;

            SelectedDestination = pickedDestination;
        }

        var error = Validate();
        if (error is not null) { ErrorMessage = error; IsErrorOpen = true; return; }
        IsSaving = true;
        IsErrorOpen = false;
        try
        {
            var contact = BuildContact();
            var requests = new List<ContactOperationPreparationRequest>
            {
                new(
                IsEditMode ? ContactSynchronizerOperation.Update : ContactSynchronizerOperation.Create,
                contact,
                _original)
            };

            if (_photoBytes is not null)
            {
                requests.Add(new ContactOperationPreparationRequest(
                    ContactSynchronizerOperation.SetPhoto,
                    contact,
                    _original ?? contact,
                    _photoBytes));
            }
            else if (_deletePhoto)
            {
                contact.ContactPictureFileId = null;
                requests.Add(new ContactOperationPreparationRequest(
                    ContactSynchronizerOperation.DeletePhoto,
                    contact,
                    _original ?? contact));
            }

            // Photo bytes stay in the request. The selected strategy persists the file only
            // after the provider accepts the mutation.
            await _requestDelegator.ExecuteAsync(requests).ConfigureAwait(false);

            var desiredListIds = ListMemberships
                .Where(item => item.IsMember)
                .Select(item => item.ListId)
                .ToArray();

            await _requestDelegator.ExecuteLocalAsync(new ApplicationLocalContactRequest(
                ApplicationLocalContactOperation.SetMemberships,
                contact: contact,
                desiredListIds: desiredListIds,
                originalListIds: _originalListIds)).ConfigureAwait(false);

            if (_preferencesService is not null)
                _preferencesService.LastUsedContactAddressBookId = contact.AddressBookId;

            await ExecuteUIThread(() =>
            {
                IsDirty = false;

                // The save already happened, so the discard prompt must not run on the way out.
                _isSaveInProgress = true;
                _navigationService.SetNavigationResult(NavigationResult.Saved(contact.Id));
                _navigationService.GoBack();
            });
        }
        catch (Exception ex)
        {
            await ExecuteUIThread(() =>
            {
                ErrorMessage = ex.Message;
                IsErrorOpen = true;
            });
        }
        finally
        {
            await ExecuteUIThread(() => IsSaving = false);
        }
    }

    /// <summary>
    /// Guards every route out of the editor: the cancel button, the shell back button and
    /// any programmatic back navigation all land here.
    /// </summary>
    public async ValueTask<bool> CanNavigateBackAsync()
    {
        if (_isSaveInProgress || !IsDirty)
            return true;

        return await _dialogService.ShowConfirmationDialogAsync(
            Translator.ContactEditor_DiscardTitle,
            Translator.ContactEditor_DiscardMessage,
            Translator.ContactEditor_DiscardAction);
    }

    [RelayCommand]
    private Task BackAsync() => _navigationService.GoBackAsync();

    [RelayCommand] private void AddEmail() { if (EmailAddresses.Count < 3) EmailAddresses.Add(new ContactEmailAddress { Id = Guid.NewGuid() }); IsDirty = true; }
    [RelayCommand] private void RemoveEmail(ContactEmailAddress item) { if (item is not null) EmailAddresses.Remove(item); IsDirty = true; }
    [RelayCommand] private void AddPhone() { PhoneNumbers.Add(new ContactPhoneNumber { Id = Guid.NewGuid(), Kind = ContactPhoneKind.Home }); IsDirty = true; }
    [RelayCommand] private void RemovePhone(ContactPhoneNumber item) { if (item is not null) PhoneNumbers.Remove(item); IsDirty = true; }
    [RelayCommand] private void AddImAddress() { ImAddresses.Add(new ContactImAddress { Id = Guid.NewGuid() }); IsDirty = true; }
    [RelayCommand] private void RemoveImAddress(ContactImAddress item) { if (item is not null) ImAddresses.Remove(item); IsDirty = true; }
    [RelayCommand] private void AddChild() { Relations.Add(new ContactRelation { Id = Guid.NewGuid(), Kind = ContactRelationKind.Child }); IsDirty = true; }
    [RelayCommand] private void RemoveRelation(ContactRelation item) { if (item is not null) Relations.Remove(item); IsDirty = true; }
    [RelayCommand]
    private async Task ChoosePhotoAsync()
    {
        var files = await _dialogService.PickFilesAsync(".png", ".jpg", ".jpeg");
        _photoBytes = files?.FirstOrDefault()?.Data;

        if (_photoBytes is null)
            return;

        _deletePhoto = false;
        IsDirty = true;
        OnPropertyChanged(nameof(PreviewPhotoBytes));
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        _photoBytes = null;
        _previewPhotoPath = null;
        _deletePhoto = true;
        IsDirty = true;
        OnPropertyChanged(nameof(PreviewPhotoBytes));
        OnPropertyChanged(nameof(PreviewPhotoPath));
    }

    public void MarkDirty() => IsDirty = true;

    private string Validate()
    {
        if (!IsEditMode && SelectedDestination is null)
            return ValidationError(ContactEditorCategory.ContactInformation, Translator.ContactEditor_DestinationRequired);

        if (string.IsNullOrWhiteSpace(DisplayName) && string.IsNullOrWhiteSpace(CompanyName) && EmailAddresses.All(item => string.IsNullOrWhiteSpace(item.Address)) && PhoneNumbers.All(item => string.IsNullOrWhiteSpace(item.Number)))
            return ValidationError(ContactEditorCategory.ContactInformation, Translator.ContactEditor_DisplayValueRequired);

        var emails = EmailAddresses.Where(item => !string.IsNullOrWhiteSpace(item.Address)).Select(item => item.Address.Trim()).ToList();

        if (emails.Any(email => !MailAddress.TryCreate(email, out _)))
            return ValidationError(ContactEditorCategory.ContactInformation, Translator.ContactEditor_InvalidEmail);

        if (emails.Distinct(StringComparer.OrdinalIgnoreCase).Count() != emails.Count)
            return ValidationError(ContactEditorCategory.ContactInformation, Translator.ContactEditor_DuplicateValue);

        if (!IsBirthdayValid())
            return ValidationError(ContactEditorCategory.Other, Translator.ContactEditor_InvalidBirthday);

        return null;
    }

    private string ValidationError(ContactEditorCategory category, string message)
    {
        SelectedCategory = category;

        return message;
    }

    private bool IsBirthdayValid()
    {
        if (BirthdayMonth is null && BirthdayDay is null && BirthdayYear is null)
            return true;

        if (BirthdayMonth is not (>= 1 and <= 12) || BirthdayDay is not (>= 1 and <= 31))
            return false;

        if (BirthdayYear is < 1 or > 9999)
            return false;

        return BirthdayDay.Value <= DateTime.DaysInMonth(BirthdayYear ?? 2000, BirthdayMonth.Value);
    }

    private AccountContact BuildContact()
    {
        var destination = SelectedDestination;
        var contact = new AccountContact
        {
            Id = _original?.Id ?? Guid.NewGuid(), MailAccountId = _original?.MailAccountId ?? destination.MailAccountId,
            AddressBookId = _original?.AddressBookId ?? destination.AddressBookId, SourceKind = _original?.SourceKind ?? destination.SourceKind,
            RemoteId = _original?.RemoteId, RemoteVersion = _original?.RemoteVersion, RemotePhotoKey = _original?.RemotePhotoKey,
            ContactPictureFileId = _deletePhoto ? null : _original?.ContactPictureFileId,
            DisplayName = DisplayName?.Trim(), HonorificPrefix = HonorificPrefix?.Trim(), GivenName = GivenName?.Trim(), MiddleName = MiddleName?.Trim(), Surname = Surname?.Trim(), HonorificSuffix = HonorificSuffix?.Trim(),
            Nickname = Nickname?.Trim(), FileAs = FileAs?.Trim(), CompanyName = CompanyName?.Trim(), Department = Department?.Trim(), JobTitle = JobTitle?.Trim(), OfficeLocation = OfficeLocation?.Trim(), Profession = Profession?.Trim(),
            BirthdayYear = BirthdayYear, BirthdayMonth = BirthdayMonth, BirthdayDay = BirthdayDay, Website = Website?.Trim(), Notes = Notes?.Trim(),
            IsFavorite = IsFavorite,
            EmailAddresses = EmailAddresses.ToList(), PhoneNumbers = PhoneNumbers.ToList(), PostalAddresses = PostalAddresses.Where(address => new[] { address.Street, address.City, address.Region, address.PostalCode, address.Country, address.PostOfficeBox }.Any(value => !string.IsNullOrWhiteSpace(value))).ToList(), ImAddresses = ImAddresses.ToList(), Relations = Relations.ToList()
        };
        if (!string.IsNullOrWhiteSpace(ManagerName)) contact.Relations.Add(new ContactRelation { Id = Guid.NewGuid(), Kind = ContactRelationKind.Manager, Name = ManagerName.Trim() });
        if (!string.IsNullOrWhiteSpace(AssistantName)) contact.Relations.Add(new ContactRelation { Id = Guid.NewGuid(), Kind = ContactRelationKind.Assistant, Name = AssistantName.Trim() });
        if (!string.IsNullOrWhiteSpace(SpouseName)) contact.Relations.Add(new ContactRelation { Id = Guid.NewGuid(), Kind = ContactRelationKind.Spouse, Name = SpouseName.Trim() });
        return contact;
    }

    private void Load(AccountContact contact)
    {
        DisplayName = contact.DisplayName; HonorificPrefix = contact.HonorificPrefix; GivenName = contact.GivenName; MiddleName = contact.MiddleName; Surname = contact.Surname; HonorificSuffix = contact.HonorificSuffix;
        Nickname = contact.Nickname; FileAs = contact.FileAs; CompanyName = contact.CompanyName; Department = contact.Department; JobTitle = contact.JobTitle; OfficeLocation = contact.OfficeLocation; Profession = contact.Profession;
        BirthdayYear = contact.BirthdayYear; BirthdayMonth = contact.BirthdayMonth; BirthdayDay = contact.BirthdayDay; Website = contact.Website; Notes = contact.Notes;
        foreach (var item in contact.EmailAddresses) EmailAddresses.Add(item);
        foreach (var item in contact.PhoneNumbers) PhoneNumbers.Add(item);
        foreach (var kind in Enum.GetValues<ContactPostalAddressKind>()) PostalAddresses.Add(contact.PostalAddresses.FirstOrDefault(item => item.Kind == kind) ?? new ContactPostalAddress { Id = Guid.NewGuid(), Kind = kind });
        foreach (var item in contact.ImAddresses) ImAddresses.Add(item);
        ManagerName = contact.Relations.FirstOrDefault(item => item.Kind == ContactRelationKind.Manager)?.Name;
        AssistantName = contact.Relations.FirstOrDefault(item => item.Kind == ContactRelationKind.Assistant)?.Name;
        SpouseName = contact.Relations.FirstOrDefault(item => item.Kind == ContactRelationKind.Spouse)?.Name;
        foreach (var item in contact.Relations.Where(item => item.Kind == ContactRelationKind.Child)) Relations.Add(item);
    }

    private void OnPreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewDisplayName));
        OnPropertyChanged(nameof(PreviewSubtitle));
    }

    partial void OnDisplayNameChanged(string value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnGivenNameChanged(string value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnSurnameChanged(string value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnCompanyNameChanged(string value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnNotesChanged(string value) => IsDirty = true;
    partial void OnSelectedDestinationChanged(ContactCreateDestination value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnHonorificPrefixChanged(string value) => IsDirty = true;
    partial void OnMiddleNameChanged(string value) => IsDirty = true;
    partial void OnHonorificSuffixChanged(string value) => IsDirty = true;
    partial void OnNicknameChanged(string value) => IsDirty = true;
    partial void OnFileAsChanged(string value) => IsDirty = true;
    partial void OnDepartmentChanged(string value) => IsDirty = true;
    partial void OnJobTitleChanged(string value) { IsDirty = true; OnPreviewChanged(); }
    partial void OnOfficeLocationChanged(string value) => IsDirty = true;
    partial void OnProfessionChanged(string value) => IsDirty = true;
    partial void OnWebsiteChanged(string value) => IsDirty = true;
    partial void OnManagerNameChanged(string value) => IsDirty = true;
    partial void OnAssistantNameChanged(string value) => IsDirty = true;
    partial void OnSpouseNameChanged(string value) => IsDirty = true;
    partial void OnIsFavoriteChanged(bool value) => IsDirty = true;

    [RelayCommand] private void ToggleFavorite() => IsFavorite = !IsFavorite;
}
