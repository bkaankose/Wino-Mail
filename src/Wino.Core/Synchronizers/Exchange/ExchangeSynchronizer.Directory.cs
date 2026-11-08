using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

// The organization's Active Directory as Exchange exposes it: the account's own photo, the photos of senders from the
// same organization, and the Global Address List as a read-only address book. Photos are fetched over EWS
// (GetUserPhoto) for both transports, since every Exchange account keeps its EWS URL.
public partial class ExchangeSynchronizer
{
    /// <summary>The remote id of the read-only address book that mirrors the Global Address List.</summary>
    protected const string DirectoryBookRemoteId = "wino:directory";

    protected const int DirectoryMaxEntries = 20000;

    private const string DirectoryContactIdPrefix = "dir:";
    private const string UserPhotoSize = "HR96x96";
    private const int SenderPhotoLookupBatchSize = 25;

    private static readonly TimeSpan DirectoryRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SenderPhotoLookupInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SenderPhotoLookupSpacing = TimeSpan.FromMilliseconds(500);

    private int _isSenderPhotoLookupRunning;
    private DateTime _lastSenderPhotoLookupUtc;
    private bool _isEwsDirectoryUnavailableLogged;

    #region Profile picture

    public override async Task<ProfileInformation> GetProfileInformationAsync()
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var picture = await FetchUserPhotoAsync(service, Account.Address, CancellationToken.None).ConfigureAwait(false);

        return new ProfileInformation(Account.SenderName, picture, null);
    }

    /// <summary>
    /// The directory photo of <paramref name="address"/>. A server answer of any kind (no photo, unknown user, an old
    /// server without GetUserPhoto) is a confirmed absence; only a failed request is a failure worth retrying.
    /// </summary>
    protected async Task<ProfilePictureFetchResult> FetchUserPhotoAsync(ExchangeService service, string address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
            return ProfilePictureFetchResult.ConfirmedAbsent;

        try
        {
            var result = await service.GetUserPhoto(address, UserPhotoSize, string.Empty, cancellationToken).ConfigureAwait(false);

            return result?.Status == GetUserPhotoStatus.PhotoReturned && result.Photo is { Length: > 0 } photo
                ? ProfilePictureFetchResult.Downloaded(photo)
                : ProfilePictureFetchResult.ConfirmedAbsent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServiceResponseException or ServiceVersionException)
        {
            _logger.Debug(ex, "Exchange has no directory photo for {Address}.", address);
            return ProfilePictureFetchResult.ConfirmedAbsent;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange directory photo of {Address} could not be fetched.", address);
            return ProfilePictureFetchResult.FetchFailed;
        }
    }

    #endregion

    #region Sender photos

    protected override void OnMailSynchronizationCompleted(MailSynchronizationResult result)
    {
        if (ContactService is null || result.CompletedState != SynchronizationCompletedState.Success)
            return;

        var hasNewMail = result.DownloadedMessages?.Any() == true;
        if (!hasNewMail && DateTime.UtcNow - _lastSenderPhotoLookupUtc < SenderPhotoLookupInterval)
            return;

        if (Interlocked.Exchange(ref _isSenderPhotoLookupRunning, 1) != 0)
            return;

        _lastSenderPhotoLookupUtc = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                await LookUpSenderPhotosAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Exchange sender photo lookup for {Account} stopped.", Account.Name);
            }
            finally
            {
                Volatile.Write(ref _isSenderPhotoLookupRunning, 0);
            }
        });
    }

    /// <summary>
    /// Looks up a batch of senders from the account's own domain that were never looked up and have no picture. Each
    /// answer is stored, a missing photo included, so no address is asked about twice. A failed request ends the
    /// batch without recording anything; the next pass retries.
    /// </summary>
    private async Task LookUpSenderPhotosAsync()
    {
        var contactService = ContactService;
        var domain = GetAddressDomain(Account.Address);
        if (contactService is null || domain is null)
            return;

        var candidates = await contactService.GetDirectoryPhotoCandidatesAsync(Account.Id, domain, SenderPhotoLookupBatchSize).ConfigureAwait(false);
        if (candidates.Count == 0)
            return;

        var service = await CreateServiceAsync().ConfigureAwait(false);
        var found = 0;

        foreach (var address in candidates)
        {
            var result = await FetchUserPhotoAsync(service, address, CancellationToken.None).ConfigureAwait(false);
            if (result.Status == ProfilePictureFetchStatus.FetchFailed)
                break;

            var photo = result.Status == ProfilePictureFetchStatus.Downloaded ? result.ImageData : null;
            await contactService.SaveDirectoryPhotoLookupAsync(Account.Id, address, photo).ConfigureAwait(false);
            if (photo != null)
                found++;

            await Task.Delay(SenderPhotoLookupSpacing).ConfigureAwait(false);
        }

        _logger.Information("Exchange sender photos for {Account}: {Found} of {Count} senders have a directory photo.", Account.Name, found, candidates.Count);
    }

    private static string? GetAddressDomain(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at > 0 && at < address!.Length - 1 ? address[(at + 1)..].Trim() : null;
    }

    #endregion

    #region Directory contacts

    /// <summary>
    /// Refreshes the read-only directory book after the account's own contacts, as a snapshot. It runs at most every
    /// half hour unless the directory book itself is asked for, and never fails the contact pass.
    /// </summary>
    private async Task SynchronizeDirectoryContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var contactService = RequireContactService();
        var existing = (await contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false))
            .FirstOrDefault(IsDirectoryBook);

        var isDirectoryRequested = existing != null && options.AddressBookId == existing.Id;
        if (options.AddressBookId is not null && !isDirectoryRequested)
            return;

        if (!isDirectoryRequested && existing?.LastSuccessfulSyncUtc is { } lastSync && DateTime.UtcNow - lastSync < DirectoryRefreshInterval)
            return;

        var contacts = await ReadDirectoryContactsAsync(cancellationToken).ConfigureAwait(false);
        if (contacts is null)
            return;

        var book = await contactService
            .GetOrCreateProviderAddressBookAsync(Account.Id, ContactSourceKind.Exchange, DirectoryBookRemoteId, Translator.ContactsPage_DirectoryAddressBook, false, null, isReadOnly: true)
            .ConfigureAwait(false);

        foreach (var contact in contacts)
            contact.AddressBookId = book.Id;

        await contactService.ReplaceAddressBookAsync(book.Id, contacts, null).ConfigureAwait(false);
        _logger.Information("Exchange directory for {Account}: {Count} entries.", Account.Name, contacts.Count);
    }

    protected static bool IsDirectoryBook(ContactAddressBook book)
        => book.SourceKind == ContactSourceKind.Exchange && string.Equals(book.RemoteId, DirectoryBookRemoteId, StringComparison.Ordinal);

    /// <summary>
    /// The directory's people, or null when this transport cannot enumerate it. EWS has no plain GAL listing; FindPeople
    /// browsing the directory folder is tried, and a server that refuses it skips the directory book.
    /// </summary>
    protected virtual async Task<List<AccountContact>?> ReadDirectoryContactsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 500;

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var contacts = new List<AccountContact>();
        var view = new ItemView(pageSize, 0);

        try
        {
            while (contacts.Count < DirectoryMaxEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var results = await service.FindPeople(WellKnownFolderName.Directory, null, view, cancellationToken).ConfigureAwait(false);
                var personas = results?.Personas?.ToList() ?? [];

                foreach (var persona in personas)
                {
                    if (!string.Equals(persona.PersonaType, "Person", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var contact = CreateDirectoryContact(
                        persona.EmailAddress?.Address,
                        persona.DisplayName,
                        persona.GivenName,
                        persona.Surname,
                        persona.CompanyName,
                        persona.Title,
                        persona.Departments?.FirstOrDefault()?.Value,
                        persona.OfficeLocations?.FirstOrDefault()?.Value,
                        []);

                    if (contact != null)
                        contacts.Add(contact);
                }

                if (personas.Count < pageSize)
                    break;

                view.Offset += personas.Count;
            }
        }
        catch (Exception ex) when (ex is ServiceResponseException or ServiceVersionException or ServiceValidationException)
        {
            if (!_isEwsDirectoryUnavailableLogged)
            {
                _isEwsDirectoryUnavailableLogged = true;
                _logger.Information(ex, "Exchange directory for {Account}: the server does not list its directory over EWS; the directory book is skipped.", Account.Name);
            }

            return null;
        }

        return contacts;
    }

    /// <summary>A directory entry as a contact keyed by its SMTP address, or null for an entry without one.</summary>
    protected AccountContact? CreateDirectoryContact(
        string? emailAddress,
        string? displayName,
        string? givenName,
        string? surname,
        string? companyName,
        string? jobTitle,
        string? department,
        string? officeLocation,
        IReadOnlyList<(string? Number, ContactPhoneKind Kind)> phones)
    {
        var address = emailAddress?.Trim();
        if (string.IsNullOrEmpty(address) || !address.Contains('@'))
            return null;

        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = DirectoryContactIdPrefix + address.ToLowerInvariant(),
            DisplayName = NullIfEmpty(displayName) ?? address,
            GivenName = NullIfEmpty(givenName),
            Surname = NullIfEmpty(surname),
            CompanyName = NullIfEmpty(companyName),
            JobTitle = NullIfEmpty(jobTitle),
            Department = NullIfEmpty(department),
            OfficeLocation = NullIfEmpty(officeLocation),
            PendingMutation = ContactPendingMutation.None
        };

        contact.EmailAddresses.Add(new ContactEmailAddress
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Address = address,
            NormalizedAddress = ContactEmailAddress.Normalize(address),
            Order = 0,
            IsPrimary = true
        });

        foreach (var (number, kind) in phones)
            AddPhone(contact, number, kind);

        return contact;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    #endregion
}
