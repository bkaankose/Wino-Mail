using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Tasks;
using EwsTask = Microsoft.Exchange.WebServices.Data.Task;
using EwsTaskStatus = Microsoft.Exchange.WebServices.Data.TaskStatus;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

public partial class ExchangeSynchronizer
{
    private const int ItemDownloadPageSize = 200;

    #region Contacts

    // The mailbox's default Contacts folder is the account's one provider address book. Address books are not managed
    // from this client, and a contact photo is not written back.

    protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Local => _localContactSynchronizer.SynchronizeAsync(options, cancellationToken),
            AccountIntegrationSource.Provider when Account.IsContactAccessGranted && ContactService is not null => SynchronizeProviderAndDirectoryContactsAsync(options, cancellationToken),
            _ => Task.FromResult(ContactSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)))
        };

    private async Task<ContactSynchronizationResult> SynchronizeProviderAndDirectoryContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var result = await SynchronizeProviderContactsAsync(options, cancellationToken).ConfigureAwait(false);

        try
        {
            await SynchronizeDirectoryContactsAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Exchange directory contacts for {Account} were not synchronized.", Account.Name);
        }

        return result;
    }

    protected override Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Local => _localContactSynchronizer.ExecuteRequestsAsync(requests, cancellationToken),
            AccountIntegrationSource.Provider when Account.IsContactAccessGranted && ContactService is not null => ExecuteProviderContactRequestsAsync(requests, cancellationToken),
            _ => throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)
        };

    protected IContactService RequireContactService()
        => ContactService ?? throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable);

    protected ITaskService RequireTaskService()
        => TaskService ?? throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

    /// <summary>
    /// The account's Exchange address book, created on first use. The account has one, the mailbox's Contacts folder,
    /// so a book stored under another id is that folder as the other transport addressed it: it is re-pointed in place
    /// rather than dropped and imported again.
    /// </summary>
    protected async Task<ContactAddressBook> GetOrCreateContactsBookAsync(string remoteFolderId)
    {
        var contactService = RequireContactService();
        var books = (await contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false))
            .Where(b => b.SourceKind == ContactSourceKind.Exchange && !IsDirectoryBook(b))
            .ToList();
        var current = books.FirstOrDefault(b => string.Equals(b.RemoteId, remoteFolderId, StringComparison.Ordinal));

        if (current is null && books.OrderByDescending(b => b.IsDefault).FirstOrDefault() is { } carried)
        {
            _logger.Information("Exchange contacts {Account}: re-keying the address book to the current transport.", Account.Name);
            await contactService.RekeyAddressBookAsync(carried.Id, remoteFolderId, new Dictionary<Guid, string>()).ConfigureAwait(false);
            current = carried;
        }

        foreach (var stale in books.Where(b => b.Id != current?.Id))
            await contactService.DeleteAddressBookAsync(stale.Id).ConfigureAwait(false);

        return await contactService.GetOrCreateProviderAddressBookAsync(Account.Id, ContactSourceKind.Exchange, remoteFolderId, Account.Name, true).ConfigureAwait(false);
    }

    /// <summary>
    /// Before a snapshot replaces the book's rows, contacts the other transport stored are matched to the incoming ones
    /// and take their remote ids, so the rebuild (which keeps rows by remote id) preserves their favourites, list
    /// memberships and local identity across a transport change.
    /// </summary>
    protected async Task RekeyContactsFromOtherTransportAsync(ContactAddressBook book, IReadOnlyList<AccountContact> incoming)
    {
        var contactService = RequireContactService();
        var stored = await contactService.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false);
        var matches = ExchangeTransportRekey.MatchContacts(stored, incoming);

        if (matches.Count == 0)
            return;

        _logger.Information("Exchange contacts {Account}: re-keying {Count} contacts stored by the other transport.", Account.Name, matches.Count);
        await contactService.RekeyAddressBookAsync(book.Id, book.RemoteId, matches).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a contact's photo when the server has one and the local row does not yet, or the photo changed since. The
    /// fetch is best effort: a failure never blocks the contact.
    /// </summary>
    protected async Task DownloadContactPhotosAsync(IReadOnlyList<(AccountContact Contact, Func<Task<byte[]?>> Fetch)> photos, ContactAddressBook book, CancellationToken cancellationToken)
    {
        if (photos.Count == 0 || ContactPictureService is null)
            return;

        var existing = (await RequireContactService().GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false))
            .Where(c => !string.IsNullOrEmpty(c.RemoteId))
            .GroupBy(c => c.RemoteId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (contact, fetch) in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.TryGetValue(contact.RemoteId, out var current) && current.ContactPictureFileId is not null &&
                string.Equals(current.RemotePhotoKey, contact.RemotePhotoKey, StringComparison.Ordinal))
            {
                contact.ContactPictureFileId = current.ContactPictureFileId;
                continue;
            }

            try
            {
                var bytes = await fetch().ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                    contact.ContactPictureFileId = await ContactPictureService.SavePictureAsync(PictureKind.Contact, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Debug(ex, "Exchange contact photo of {RemoteId} not downloaded.", contact.RemoteId);
            }
        }
    }

    // FirstClassProperties loads the full contact (names, e-mail addresses, phones, addresses, company, notes) in one
    // batch; the attachments tell whether a contact photo exists.
    private static readonly PropertySet ContactPropertySet = new(BasePropertySet.FirstClassProperties, ItemSchema.Attachments)
    {
        RequestedBodyType = BodyType.Text
    };

    /// <summary>
    /// One-way pull of the default Contacts folder into the address book: page the items, batch-load their properties,
    /// then replace the book's rows. Favourites and downloaded pictures survive the rebuild by remote id.
    /// </summary>
    protected virtual async Task<ContactSynchronizationResult> SynchronizeProviderContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var contactsFolder = await ContactsFolder
            .Bind(service, WellKnownFolderName.Contacts, new PropertySet(BasePropertySet.IdOnly))
            .ConfigureAwait(false);

        var book = await GetOrCreateContactsBookAsync(contactsFolder.Id.UniqueId).ConfigureAwait(false);
        var upserts = new List<AccountContact>();
        var photos = new List<(AccountContact, Func<Task<byte[]?>>)>();

        var view = new ItemView(ItemDownloadPageSize);
        FindItemsResults<Item> results;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            results = await service.FindItems(contactsFolder.Id, view).ConfigureAwait(false);

            var contacts = results.Items.OfType<Contact>().ToList();
            if (contacts.Count > 0)
            {
                await service.LoadPropertiesForItems(contacts, ContactPropertySet).ConfigureAwait(false);

                foreach (var contact in contacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapped = MapToAccountContact(contact, book);
                    upserts.Add(mapped);

                    if (BuildContactPhotoFetcher(contact) is { } fetch)
                        photos.Add((mapped, fetch));
                }
            }

            view.Offset += results.Items.Count;
        }
        while (results.MoreAvailable);

        await RekeyContactsFromOtherTransportAsync(book, upserts).ConfigureAwait(false);
        await DownloadContactPhotosAsync(photos, book, cancellationToken).ConfigureAwait(false);
        await RequireContactService().ReplaceAddressBookAsync(book.Id, upserts, null).ConfigureAwait(false);

        _logger.Information("Exchange contacts sync for {Account}: {Count} server contacts.", Account.Name, upserts.Count);

        return ContactSynchronizationResult.Completed(upserts.Count, upserts.Count, 0);
    }

    protected virtual async Task ExecuteProviderContactRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await ResolveRequestedContactAsync(request).ConfigureAwait(false);

            switch (request.Operation)
            {
                case ContactSynchronizerOperation.Create when local is not null:
                {
                    var contact = new Contact(service);
                    ApplyContactProperties(contact, local);
                    await contact.Save(WellKnownFolderName.Contacts).ConfigureAwait(false);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteId = contact.Id.UniqueId;
                    mapped.RemoteVersion = contact.Id.ChangeKey;
                    mapped.SourceKind = ContactSourceKind.Exchange;
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Update when local is not null:
                {
                    var contact = await Contact.Bind(service, new ItemId(local.RemoteId)).ConfigureAwait(false);
                    ApplyContactProperties(contact, local);
                    await contact.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteVersion = contact.Id.ChangeKey;
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Delete:
                {
                    if (!string.IsNullOrWhiteSpace(local?.RemoteId))
                    {
                        try
                        {
                            var contact = await Contact.Bind(service, new ItemId(local.RemoteId), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                            await contact.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                        }
                        catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
                        {
                            _logger.Warning("Exchange contact {RemoteId} was already gone when it was deleted.", local.RemoteId);
                        }
                    }

                    if (local is not null)
                        await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, null, true).ConfigureAwait(false);
                    break;
                }
                default:
                    throw UnsupportedContactOperation(request.Operation);
            }
        }
    }

    /// <summary>
    /// The contact a request acts on: the request's snapshot, or the stored row when the snapshot lacks the server id.
    /// Null only for a delete of a contact that no longer exists anywhere.
    /// </summary>
    protected async Task<AccountContact?> ResolveRequestedContactAsync(IContactActionRequest request)
    {
        var typedRequest = request as ContactActionRequest;
        var local = typedRequest?.Contact;

        if (request.Operation != ContactSynchronizerOperation.Create && string.IsNullOrWhiteSpace(local?.RemoteId))
        {
            local = await RequireContactService().GetContactAsync(request.LocalContactId).ConfigureAwait(false)
                    ?? typedRequest?.OriginalContact
                    ?? local;
        }

        if (local is null && request.Operation != ContactSynchronizerOperation.Delete)
            throw new InvalidOperationException($"Contact {request.LocalContactId} is unavailable for {request.Operation}.");

        if (request.Operation is ContactSynchronizerOperation.Update && string.IsNullOrWhiteSpace(local?.RemoteId))
            throw new InvalidOperationException($"Contact {request.LocalContactId} has no server identity for {request.Operation}.");

        return local;
    }

    protected static NotSupportedException UnsupportedContactOperation(ContactSynchronizerOperation operation)
        => operation is ContactSynchronizerOperation.SetPhoto or ContactSynchronizerOperation.DeletePhoto
            ? new NotSupportedException(Translator.Synchronizer_ExchangeContactPhotoUnsupported)
            : new NotSupportedException(Translator.Synchronizer_ExchangeAddressBooksUnsupported);

    // Indexed properties (e-mail addresses, phones) are only set when present, since clearing an EWS dictionary entry
    // is unreliable across server versions. Postal addresses are not written back.
    private static void ApplyContactProperties(Contact contact, AccountContact item)
    {
        contact.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.PrimaryEmailAddress : item.DisplayName;
        contact.GivenName = item.GivenName;
        contact.Surname = item.Surname;
        contact.CompanyName = item.CompanyName;
        contact.JobTitle = item.JobTitle;

        var keys = new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 };
        var addresses = (item.EmailAddresses ?? [])
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.Order)
            .Select(a => a.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Take(keys.Length)
            .ToList();

        for (var i = 0; i < addresses.Count; i++)
            contact.EmailAddresses[keys[i]] = new EmailAddress(addresses[i]);

        SetPhone(contact, PhoneNumberKey.BusinessPhone, FirstPhone(item, ContactPhoneKind.Work));
        SetPhone(contact, PhoneNumberKey.HomePhone, FirstPhone(item, ContactPhoneKind.Home));
        SetPhone(contact, PhoneNumberKey.MobilePhone, FirstPhone(item, ContactPhoneKind.Mobile));

        if (!string.IsNullOrWhiteSpace(item.Notes))
            contact.Body = new MessageBody(BodyType.Text, item.Notes);
    }

    private static string? FirstPhone(AccountContact item, ContactPhoneKind kind)
        => item.PhoneNumbers?.Where(p => p.Kind == kind).OrderBy(p => p.Order).Select(p => p.Number).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    private static void SetPhone(Contact contact, PhoneNumberKey key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            contact.PhoneNumbers[key] = value;
    }

    private AccountContact MapToAccountContact(Contact remote, ContactAddressBook book)
    {
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            AddressBookId = book.Id,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = remote.Id.UniqueId,
            RemoteVersion = remote.Id.ChangeKey,
            RemotePhotoKey = HasContactPhoto(remote) ? remote.Id.ChangeKey : null,
            DisplayName = SafeGet(() => remote.DisplayName),
            GivenName = SafeGet(() => remote.GivenName),
            MiddleName = SafeGet(() => remote.MiddleName),
            Surname = SafeGet(() => remote.Surname),
            Nickname = SafeGet(() => remote.NickName),
            FileAs = SafeGet(() => remote.FileAs),
            CompanyName = SafeGet(() => remote.CompanyName),
            Department = SafeGet(() => remote.Department),
            JobTitle = SafeGet(() => remote.JobTitle),
            OfficeLocation = SafeGet(() => remote.OfficeLocation),
            Profession = SafeGet(() => remote.Profession),
            Notes = SafeGet(() => remote.Body?.Text),
            Website = SafeGet(() => remote.BusinessHomePage),
            PendingMutation = ContactPendingMutation.None
        };

        if (SafeGet(() => remote.Birthday) is { } birthday)
        {
            contact.BirthdayYear = birthday.Year;
            contact.BirthdayMonth = birthday.Month;
            contact.BirthdayDay = birthday.Day;
        }

        var order = 0;
        foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
        {
            var address = GetEmail(remote, key);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            contact.EmailAddresses.Add(new ContactEmailAddress
            {
                Id = Guid.NewGuid(),
                ContactId = contact.Id,
                Address = address,
                NormalizedAddress = ContactEmailAddress.Normalize(address),
                Order = order,
                IsPrimary = order == 0
            });
            order++;
        }

        AddPhone(contact, GetPhone(remote, PhoneNumberKey.BusinessPhone), ContactPhoneKind.Work);
        AddPhone(contact, GetPhone(remote, PhoneNumberKey.HomePhone), ContactPhoneKind.Home);
        AddPhone(contact, GetPhone(remote, PhoneNumberKey.MobilePhone), ContactPhoneKind.Mobile);

        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Business), ContactPostalAddressKind.Business);
        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Home), ContactPostalAddressKind.Home);
        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Other), ContactPostalAddressKind.Other);

        return contact;
    }

    private static void AddPhone(AccountContact contact, string? number, ContactPhoneKind kind)
    {
        if (string.IsNullOrWhiteSpace(number))
            return;

        contact.PhoneNumbers.Add(new ContactPhoneNumber
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Number = number.Trim(),
            Kind = kind,
            Order = contact.PhoneNumbers.Count,
            IsPrimary = contact.PhoneNumbers.Count == 0
        });
    }

    private static void AddAddress(AccountContact contact, PhysicalAddressEntry? address, ContactPostalAddressKind kind)
    {
        if (address is null)
            return;

        var parts = new[] { address.Street, address.City, address.State, address.PostalCode, address.CountryOrRegion };
        if (parts.All(string.IsNullOrWhiteSpace))
            return;

        contact.PostalAddresses.Add(new ContactPostalAddress
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Kind = kind,
            Street = address.Street,
            City = address.City,
            Region = address.State,
            PostalCode = address.PostalCode,
            Country = address.CountryOrRegion
        });
    }

    // A key that was never set throws on some servers instead of reporting absence, and one such contact would
    // otherwise fail the whole book.
    private static string? GetEmail(Contact contact, EmailAddressKey key)
        => SafeGet(() => contact.EmailAddresses != null && contact.EmailAddresses.TryGetValue(key, out var email) ? email?.Address?.Trim() : null);

    private static string? GetPhone(Contact contact, PhoneNumberKey key)
        => SafeGet(() => contact.PhoneNumbers != null && contact.PhoneNumbers.TryGetValue(key, out var number) ? number : null);

    private static PhysicalAddressEntry? GetAddress(Contact contact, PhysicalAddressKey key)
        => SafeGet(() => contact.PhysicalAddresses != null && contact.PhysicalAddresses.TryGetValue(key, out var address) ? address : null);

    private static bool HasContactPhoto(Contact contact)
        => SafeGet(() => (bool?)contact.Attachments?.OfType<FileAttachment>().Any(a => a.IsContactPhoto)) ?? false;

    /// <summary>
    /// A lazy fetch of the contact's photo attachment, or null when it has none. It runs only when the photo needs
    /// storing, so contacts that already have their picture cost no extra round trip.
    /// </summary>
    private static Func<Task<byte[]?>>? BuildContactPhotoFetcher(Contact contact)
    {
        var photoAttachment = SafeGet(() => contact.Attachments?.OfType<FileAttachment>().FirstOrDefault(a => a.IsContactPhoto));
        if (photoAttachment == null)
            return null;

        // This EWS port offers only a synchronous Load, so the blocking fetch runs on the thread pool.
        return () => Task.Run<byte[]?>(() =>
        {
            photoAttachment.Load();
            return photoAttachment.Content;
        });
    }

    #endregion

    #region Tasks

    // Every task folder of the mailbox (IPF.Task, outside Deleted Items) is a task list; the well-known Tasks folder is
    // the default one. Exchange tasks have no checklist steps.

    protected const string TaskFolderClass = "IPF.Task";

    /// <summary>A task folder of the mailbox, keyed by the transport's folder id.</summary>
    protected sealed record ExchangeTaskFolder(string RemoteId, string? Title, bool IsDefault);

    protected override Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Local)
            return _localTaskSynchronizer.SynchronizeAsync(options, cancellationToken);

        if (Account.TaskIntegrationSource != AccountIntegrationSource.Provider || !Account.IsTaskAccessGranted || TaskService is null)
            return Task.FromResult(TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable)));

        return SynchronizeProviderTasksAsync(options, cancellationToken);
    }

    protected override Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Local || requests.All(IsLocalTaskRequest))
            return _localTaskSynchronizer.ExecuteRequestsAsync(requests, cancellationToken);

        if (Account.TaskIntegrationSource != AccountIntegrationSource.Provider || !Account.IsTaskAccessGranted || TaskService is null)
            throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

        return ExecuteProviderTaskRequestsAsync(requests, cancellationToken);
    }

    private static bool IsLocalTaskRequest(ITaskActionRequest request)
        => request is TaskActionRequest taskRequest &&
           (taskRequest.List?.SourceKind ??
            taskRequest.Task?.SourceKind ??
            taskRequest.Step?.SourceKind ??
            taskRequest.Group?.SourceKind) == TaskSourceKind.Local;

    protected override bool ShouldReconcileTaskRequests(IReadOnlyList<ITaskActionRequest> requests)
        => !requests.All(IsLocalTaskRequest);

    protected static bool IsTaskFolderClass(string? folderClass)
        => folderClass is not null && folderClass.StartsWith(TaskFolderClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles the account's Exchange task lists with the mailbox's task folders and returns the lists in folder
    /// order. Lists stored under the other transport's ids are re-keyed in place first (the default list to the default
    /// folder, the others by title), so colour, group, placement and the tasks' My Day dates survive a transport change.
    /// </summary>
    protected async Task<List<AccountTaskList>> EnsureTaskListsAsync(IReadOnlyList<ExchangeTaskFolder> folders)
    {
        var taskService = RequireTaskService();
        var stored = (await taskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false))
            .Where(list => list.SourceKind == TaskSourceKind.Exchange)
            .ToList();

        var rekeyed = ExchangeTransportRekey.MatchTaskLists(stored, folders.Select(folder => (folder.RemoteId, folder.Title ?? string.Empty, folder.IsDefault)).ToList());
        foreach (var (listId, remoteId) in rekeyed)
        {
            _logger.Information("Exchange tasks {Account}: re-keying task list {ListId} to the current transport.", Account.Name, listId);
            await taskService.RekeyTaskListAsync(listId, remoteId, new Dictionary<Guid, string>()).ConfigureAwait(false);
        }

        await taskService.ApplyTaskTopologyDeltaAsync(new TaskTopologyDelta
        {
            MailAccountId = Account.Id,
            SourceKind = TaskSourceKind.Exchange,
            Lists = folders.Select(folder => new AccountTaskList
            {
                MailAccountId = Account.Id,
                SourceKind = TaskSourceKind.Exchange,
                RemoteId = folder.RemoteId,
                Title = string.IsNullOrWhiteSpace(folder.Title) ? Translator.ToDoPage_Tasks : folder.Title,
                IsDefault = folder.IsDefault,
                IsReadOnly = false
            }).ToList(),
            ReconcileLists = true
        }).ConfigureAwait(false);

        var listsByRemoteId = (await taskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false))
            .Where(list => list.SourceKind == TaskSourceKind.Exchange && !string.IsNullOrWhiteSpace(list.RemoteId))
            .GroupBy(list => list.RemoteId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return folders
            .Select(folder => listsByRemoteId.GetValueOrDefault(folder.RemoteId))
            .OfType<AccountTaskList>()
            .ToList();
    }

    /// <summary>The lists a pass reads: the one a change notification named, or all of them.</summary>
    protected static List<AccountTaskList> SelectTaskListsToSynchronize(List<AccountTaskList> lists, TaskSynchronizationOptions? options)
        => options?.TaskListId is Guid listId && lists.Any(list => list.Id == listId)
            ? lists.Where(list => list.Id == listId).ToList()
            : lists;

    protected static TaskSynchronizationResult CombineTaskResults(IReadOnlyList<TaskSynchronizationResult> results)
        => TaskSynchronizationResult.Completed(
            results.Sum(result => result.DownloadedCount),
            results.Sum(result => result.ChangedCount),
            results.Sum(result => result.DeletedCount));

    /// <summary>
    /// Replaces the list's rows with the server's, keeping the stored identity of rows already known by remote id so a
    /// reconciliation racing a create does not double the task.
    /// </summary>
    protected async Task<TaskSynchronizationResult> ApplyTaskSnapshotAsync(AccountTaskList list, List<AccountTask> tasks)
    {
        var taskService = RequireTaskService();
        var current = await taskService.GetTasksAsync(listId: list.Id).ConfigureAwait(false);

        // Tasks the other transport stored take the incoming ids, so the snapshot keeps their rows (and their My Day
        // date) instead of importing them again after a transport change.
        var rekeyed = ExchangeTransportRekey.MatchTasks(current, tasks);
        if (rekeyed.Count > 0)
        {
            _logger.Information("Exchange tasks {Account}: re-keying {Count} tasks stored by the other transport.", Account.Name, rekeyed.Count);
            await taskService.RekeyTaskListAsync(list.Id, list.RemoteId, rekeyed).ConfigureAwait(false);

            foreach (var task in current)
            {
                if (rekeyed.TryGetValue(task.Id, out var remoteId))
                    task.RemoteId = remoteId;
            }
        }

        var currentByRemoteId = current.Where(task => !string.IsNullOrWhiteSpace(task.RemoteId))
            .GroupBy(task => task.RemoteId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (task.RemoteId is not null && currentByRemoteId.TryGetValue(task.RemoteId, out var existing))
                task.Id = existing.Id;
        }

        var incomingRemoteIds = tasks.Select(t => t.RemoteId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var deleted = current.Count(task => task.PendingMutation == TaskPendingMutation.None &&
                                            task.RemoteId is not null &&
                                            !incomingRemoteIds.Contains(task.RemoteId));

        await taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks = tasks,
            AuthoritativeStepParentRemoteIds = incomingRemoteIds.ToList(),
            IsFullSnapshot = true,
            WatermarkUtc = DateTime.UtcNow
        }).ConfigureAwait(false);

        return TaskSynchronizationResult.Completed(tasks.Count, tasks.Count, deleted);
    }

    /// <summary>The task a request acts on, with the stored server identity applied over the request's snapshot.</summary>
    protected async Task<(AccountTask Task, AccountTaskList List)> ResolveRequestedTaskAsync(ITaskActionRequest request)
    {
        var taskService = RequireTaskService();
        var typedRequest = request as TaskActionRequest;
        var requestedTask = typedRequest?.Task ?? typedRequest?.OriginalTask;
        var storedTask = await taskService.GetTaskAsync(request.TaskId ?? Guid.Empty).ConfigureAwait(false);
        var localTask = RequestEntityCloner.Task(requestedTask ?? storedTask)
            ?? throw new InvalidOperationException($"Task {request.TaskId} is unavailable for {request.Operation}.");

        if (storedTask is not null)
        {
            localTask.RemoteId = storedTask.RemoteId ?? localTask.RemoteId;
            localTask.RemoteVersion = storedTask.RemoteVersion ?? localTask.RemoteVersion;
        }

        var list = await taskService.GetTaskListAsync(localTask.TaskListId).ConfigureAwait(false);
        if (list?.RemoteId is null)
            throw new InvalidOperationException($"Task list {localTask.TaskListId} is unavailable for {request.Operation}.");

        return (localTask, list);
    }

    /// <summary>
    /// The list a task update moves the task out of, or null when the update keeps it in place. The server item lives in
    /// that list's folder until it is moved, so the write and the move address it there.
    /// </summary>
    protected async Task<AccountTaskList?> ResolveMoveSourceListAsync(ITaskActionRequest request, AccountTask localTask)
    {
        if (request.Operation != TaskSynchronizerOperation.UpdateTask || string.IsNullOrWhiteSpace(localTask.RemoteId))
            return null;

        var taskService = RequireTaskService();
        var storedTask = await taskService.GetTaskAsync(localTask.Id).ConfigureAwait(false);
        var original = new[] { storedTask, (request as TaskActionRequest)?.OriginalTask }
            .FirstOrDefault(task => task is not null && task.TaskListId != localTask.TaskListId);
        if (original is null)
            return null;

        var source = await taskService.GetTaskListAsync(original.TaskListId).ConfigureAwait(false);
        if (source?.RemoteId is null || source.SourceKind != TaskSourceKind.Exchange)
            throw new InvalidOperationException($"Source task list {original.TaskListId} is unavailable for moving task {localTask.Id}.");

        return source;
    }

    /// <summary>The list a list request acts on, with the stored server identity applied over the request's snapshot.</summary>
    protected async Task<AccountTaskList> ResolveRequestedTaskListAsync(ITaskActionRequest request)
    {
        var typedRequest = request as TaskActionRequest;
        var requestedList = typedRequest?.List ?? typedRequest?.OriginalList;
        var storedList = await RequireTaskService().GetTaskListAsync(request.TaskListId ?? requestedList?.Id ?? Guid.Empty).ConfigureAwait(false);
        var list = RequestEntityCloner.TaskList(requestedList ?? storedList)
            ?? throw new InvalidOperationException($"Task list {request.TaskListId} is unavailable for {request.Operation}.");

        if (storedList is not null)
        {
            list.RemoteId = storedList.RemoteId ?? list.RemoteId;
            list.IsDefault = storedList.IsDefault;
        }

        if (request.Operation != TaskSynchronizerOperation.DeleteList && string.IsNullOrWhiteSpace(list.Title))
            throw new InvalidOperationException($"Task list {list.Id} has no name for {request.Operation}.");

        return list;
    }

    /// <summary>Exchange refuses to rename or delete a mailbox's default folders; Outlook does not offer it either.</summary>
    protected static void EnsureTaskListIsNotDefault(AccountTaskList list)
    {
        if (list.IsDefault)
            throw new NotSupportedException(Translator.Synchronizer_ExchangeDefaultTaskListLocked);
    }

    /// <summary>Commits a list create or rename under the folder's server identity, keeping the list's local placement.</summary>
    protected Task CommitTaskListAsync(AccountTaskList requestedList, string remoteId)
    {
        var mapped = RequestEntityCloner.TaskList(requestedList);
        mapped.RemoteId = remoteId;
        mapped.SourceKind = TaskSourceKind.Exchange;
        mapped.Title = requestedList.Title.Trim();
        mapped.IsReadOnly = false;
        mapped.PendingMutation = TaskPendingMutation.None;

        return ExchangeChangeProcessor.CommitTaskListMutationAsync(requestedList.Id, mapped, false);
    }

    /// <summary>Commits a task write: the local snapshot under its server identity, as the Gmail and Outlook paths do.</summary>
    protected async Task CommitTaskAsync(ITaskActionRequest request, AccountTask localTask, string remoteId, string? remoteVersion)
    {
        var mapped = RequestEntityCloner.Task(localTask);
        mapped.RemoteId = remoteId;
        mapped.RemoteVersion = remoteVersion;
        mapped.SourceKind = TaskSourceKind.Exchange;
        mapped.PendingMutation = TaskPendingMutation.None;
        mapped.Steps = [];

        await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, mapped, false, localTask, request.Operation).ConfigureAwait(false);
    }

    /// <summary>
    /// Group and placement edits are local bookkeeping and complete locally, as on Gmail. Steps have no Exchange
    /// counterpart and are refused. List and task writes return false for the transport to perform.
    /// </summary>
    protected async Task<bool> TryCompleteLocalTaskRequestAsync(ITaskActionRequest request)
    {
        var taskService = RequireTaskService();
        var typedRequest = request as TaskActionRequest;

        switch (request.Operation)
        {
            case TaskSynchronizerOperation.CreateGroup or TaskSynchronizerOperation.UpdateGroup when typedRequest?.Group is { } group:
                await taskService.CompleteTaskListGroupMutationAsync(group.Id, group, false).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.DeleteGroup when typedRequest?.Group is { } deletedGroup:
                await taskService.CompleteTaskListGroupMutationAsync(deletedGroup.Id, null, true).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.UpdateListPlacement when typedRequest?.List is { } placedList:
                await taskService.CompleteTaskListPlacementMutationAsync(placedList.Id, placedList).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.CreateList:
            case TaskSynchronizerOperation.UpdateList:
            case TaskSynchronizerOperation.DeleteList:
            case TaskSynchronizerOperation.CreateTask:
            case TaskSynchronizerOperation.UpdateTask:
            case TaskSynchronizerOperation.DeleteTask:
                return false;
            case TaskSynchronizerOperation.CreateStep:
            case TaskSynchronizerOperation.UpdateStep:
            case TaskSynchronizerOperation.DeleteStep:
                throw new NotSupportedException(Translator.Synchronizer_ExchangeTaskStepsUnsupported);
            default:
                throw new NotSupportedException($"Task operation {request.Operation} has no Exchange counterpart.");
        }
    }

    private static readonly PropertySet TaskPropertySet = new(
        BasePropertySet.IdOnly,
        TaskSchema.Subject,
        TaskSchema.Body,
        TaskSchema.DueDate,
        TaskSchema.Importance,
        TaskSchema.IsComplete,
        TaskSchema.CompleteDate)
    {
        RequestedBodyType = BodyType.Text
    };

    private static readonly PropertySet TaskFolderPropertySet = new(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass);

    /// <summary>The mailbox's task folders: the default Tasks folder first, then every other one outside Deleted Items.</summary>
    private static async Task<List<ExchangeTaskFolder>> ReadEwsTaskFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var defaultFolder = await Folder.Bind(service, WellKnownFolderName.Tasks, TaskFolderPropertySet).ConfigureAwait(false);
        var filter = new SearchFilter.ContainsSubstring(FolderSchema.FolderClass, TaskFolderClass, ContainmentMode.Prefixed, ComparisonMode.IgnoreCase);

        var discarded = (await FindEwsFoldersAsync(service, WellKnownFolderName.DeletedItems, filter, cancellationToken).ConfigureAwait(false))
            .Select(folder => folder.Id.UniqueId)
            .ToHashSet(StringComparer.Ordinal);

        var folders = new List<ExchangeTaskFolder> { new(defaultFolder.Id.UniqueId, SafeGet(() => defaultFolder.DisplayName), true) };
        foreach (var folder in await FindEwsFoldersAsync(service, WellKnownFolderName.MsgFolderRoot, filter, cancellationToken).ConfigureAwait(false))
        {
            var remoteId = folder.Id.UniqueId;
            if (folders.Any(existing => string.Equals(existing.RemoteId, remoteId, StringComparison.Ordinal)) || discarded.Contains(remoteId))
                continue;

            folders.Add(new ExchangeTaskFolder(remoteId, SafeGet(() => folder.DisplayName), false));
        }

        return folders;
    }

    private static async Task<List<Folder>> FindEwsFoldersAsync(ExchangeService service, WellKnownFolderName root, SearchFilter filter, CancellationToken cancellationToken)
    {
        var folders = new List<Folder>();
        var view = new FolderView(500) { Traversal = FolderTraversal.Deep, PropertySet = TaskFolderPropertySet };
        FindFoldersResults page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = await service.FindFolders(root, filter, view).ConfigureAwait(false);
            folders.AddRange(page.Folders);
            view.Offset += page.Folders.Count;
        }
        while (page.MoreAvailable && page.Folders.Count > 0);

        return folders;
    }

    /// <summary>One-way pull of every task folder into its task list, as the contacts pull does.</summary>
    protected virtual async Task<TaskSynchronizationResult> SynchronizeProviderTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var lists = await EnsureTaskListsAsync(await ReadEwsTaskFoldersAsync(service, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        var results = new List<TaskSynchronizationResult>();
        foreach (var list in SelectTaskListsToSynchronize(lists, options))
        {
            var tasks = new List<AccountTask>();
            var view = new ItemView(ItemDownloadPageSize);
            FindItemsResults<Item> page;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                page = await service.FindItems(new FolderId(list.RemoteId), view).ConfigureAwait(false);

                var items = page.Items.OfType<EwsTask>().ToList();
                if (items.Count > 0)
                {
                    await service.LoadPropertiesForItems(items, TaskPropertySet).ConfigureAwait(false);

                    foreach (var task in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tasks.Add(MapToAccountTask(task, list));
                    }
                }

                view.Offset += page.Items.Count;
            }
            while (page.MoreAvailable);

            results.Add(await ApplyTaskSnapshotAsync(list, tasks).ConfigureAwait(false));
        }

        var result = CombineTaskResults(results);
        _logger.Information("Exchange tasks sync for {Account}: {Lists} of {AllLists} lists, {Count} server tasks, {Removed} removed.",
            Account.Name, results.Count, lists.Count, result.DownloadedCount, result.DeletedCount);

        return result;
    }

    protected virtual async Task ExecuteProviderTaskRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryCompleteLocalTaskRequestAsync(request).ConfigureAwait(false))
            {
                MarkTaskRequestProcessed(request);
                continue;
            }

            switch (request.Operation)
            {
                case TaskSynchronizerOperation.CreateList:
                    await CreateEwsTaskListAsync(service, request).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.UpdateList:
                    await RenameEwsTaskListAsync(service, request).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.DeleteList:
                    await DeleteEwsTaskListAsync(service, request).ConfigureAwait(false);
                    break;
                default:
                    await ExecuteEwsTaskMutationAsync(service, request).ConfigureAwait(false);
                    break;
            }

            MarkTaskRequestProcessed(request);
        }
    }

    private async Task CreateEwsTaskListAsync(ExchangeService service, ITaskActionRequest request)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        var folder = new Folder(service) { DisplayName = list.Title.Trim(), FolderClass = TaskFolderClass };

        try
        {
            await folder.Save(WellKnownFolderName.Tasks).ConfigureAwait(false);
        }
        catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorFolderExists)
        {
            throw new InvalidOperationException(Translator.Synchronizer_ExchangeTaskListNameExists, ex);
        }

        await CommitTaskListAsync(list, folder.Id.UniqueId).ConfigureAwait(false);
    }

    private async Task RenameEwsTaskListAsync(ExchangeService service, ITaskActionRequest request)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        EnsureTaskListIsNotDefault(list);
        if (string.IsNullOrWhiteSpace(list.RemoteId))
            throw new InvalidOperationException($"Task list {list.Id} has no Exchange folder to rename.");

        var folder = await Folder.Bind(service, new FolderId(list.RemoteId), new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName)).ConfigureAwait(false);
        folder.DisplayName = list.Title.Trim();

        try
        {
            await folder.Update().ConfigureAwait(false);
        }
        catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorFolderExists)
        {
            throw new InvalidOperationException(Translator.Synchronizer_ExchangeTaskListNameExists, ex);
        }

        await CommitTaskListAsync(list, list.RemoteId).ConfigureAwait(false);
    }

    /// <summary>The folder goes to Deleted Items with its tasks, as Outlook deletes a task folder.</summary>
    private async Task DeleteEwsTaskListAsync(ExchangeService service, ITaskActionRequest request)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        EnsureTaskListIsNotDefault(list);

        if (!string.IsNullOrWhiteSpace(list.RemoteId))
        {
            try
            {
                var folder = await Folder.Bind(service, new FolderId(list.RemoteId), new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName)).ConfigureAwait(false);
                try
                {
                    await folder.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                }
                catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorFolderExists)
                {
                    // Deleted Items already holds a folder of this name; a dated name lets this one join it.
                    folder.DisplayName = $"{folder.DisplayName} ({DateTime.Now:yyyy-MM-dd HH.mm.ss})";
                    await folder.Update().ConfigureAwait(false);
                    await folder.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                }
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Exchange task folder of list {ListId} was already gone when it was deleted.", list.Id);
            }
        }

        await ExchangeChangeProcessor.CommitTaskListMutationAsync(list.Id, null, true).ConfigureAwait(false);
    }

    private async Task ExecuteEwsTaskMutationAsync(ExchangeService service, ITaskActionRequest request)
    {
        var (localTask, list) = await ResolveRequestedTaskAsync(request).ConfigureAwait(false);

        if (request.Operation == TaskSynchronizerOperation.DeleteTask)
        {
            if (!string.IsNullOrWhiteSpace(localTask.RemoteId))
            {
                try
                {
                    var task = await EwsTask.Bind(service, new ItemId(localTask.RemoteId), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                    await task.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                }
                catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
                {
                    _logger.Warning("Exchange task {RemoteTaskId} was already gone when it was deleted.", localTask.RemoteId);
                }
            }

            await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
            return;
        }

        var source = await ResolveMoveSourceListAsync(request, localTask).ConfigureAwait(false);
        ItemId itemId;
        if (string.IsNullOrWhiteSpace(localTask.RemoteId))
        {
            var created = new EwsTask(service);
            ApplyTaskProperties(created, localTask, isNew: true);
            await created.Save(new FolderId(list.RemoteId)).ConfigureAwait(false);
            itemId = created.Id;
        }
        else
        {
            var existing = await EwsTask.Bind(service, new ItemId(localTask.RemoteId)).ConfigureAwait(false);
            ApplyTaskProperties(existing, localTask, isNew: false);
            await existing.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            itemId = existing.Id;

            if (source is not null)
            {
                var moved = await existing.Move(new FolderId(list.RemoteId)).ConfigureAwait(false);
                itemId = moved?.Id ?? itemId;
            }
        }

        // Tasks have no natural key: the server id is stamped on the local row so the next pull reconciles it in
        // place instead of creating a duplicate.
        await CommitTaskAsync(request, localTask, itemId.UniqueId, itemId.ChangeKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the local fields. The local model has no start date, no in-progress state and no low importance, so an
    /// update keeps the server's values for those unless the edit contradicts them.
    /// </summary>
    private static void ApplyTaskProperties(EwsTask task, AccountTask item, bool isNew)
    {
        task.Subject = item.Title ?? string.Empty;
        task.Body = new MessageBody(BodyType.Text, item.Notes ?? string.Empty);

        if (item.DueDate is DateTime due)
        {
            DateTime? start = null;
            if (!isNew && task.TryGetProperty(TaskSchema.StartDate, out DateTime? currentStart))
                start = currentStart;

            task.DueDate = due;
            if (start is null || start.Value.Date > due.Date)
                task.StartDate = due;
        }

        var currentImportance = isNew ? null : SafeGet(() => (Importance?)task.Importance);
        task.Importance = item.IsImportant
            ? Importance.High
            : currentImportance == Importance.Low ? Importance.Low : Importance.Normal;

        var currentStatus = isNew ? null : SafeGet(() => (EwsTaskStatus?)task.Status);
        if (item.IsCompleted)
            task.Status = EwsTaskStatus.Completed;
        else if (currentStatus is null or EwsTaskStatus.Completed)
            task.Status = EwsTaskStatus.NotStarted;
    }

    private AccountTask MapToAccountTask(EwsTask task, AccountTaskList list)
    {
        var completed = SafeGet(() => (bool?)task.IsComplete) ?? false;

        // TryGetProperty needs the nullable DateTime as its type argument; a plain DateTime throws.
        return new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Exchange,
            RemoteId = task.Id.UniqueId,
            RemoteVersion = task.Id.ChangeKey,
            Title = SafeGet(() => task.Subject) ?? string.Empty,
            Notes = SafeGet(() => task.Body?.Text),
            DueDate = task.TryGetProperty(TaskSchema.DueDate, out DateTime? dueDate) ? dueDate?.Date : null,
            IsImportant = SafeGet(() => (Importance?)task.Importance) == Importance.High,
            IsCompleted = completed,
            CompletedAtUtc = completed && task.TryGetProperty(TaskSchema.CompleteDate, out DateTime? completeDate) ? completeDate : null,
            PendingMutation = TaskPendingMutation.None
        };
    }

    #endregion
}
