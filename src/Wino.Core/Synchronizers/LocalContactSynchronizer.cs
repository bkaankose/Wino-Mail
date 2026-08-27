using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Completes contact requests that were already applied to an account-local address book.
/// It intentionally owns no HTTP client and performs no network operation.
/// </summary>
public sealed class LocalContactSynchronizer
{
    private readonly IContactService _contactService;
    private readonly IContactPictureFileService _contactPictureFileService;

    public LocalContactSynchronizer(
        IContactService contactService = null,
        IContactPictureFileService contactPictureFileService = null)
    {
        _contactService = contactService;
        _contactPictureFileService = contactPictureFileService;
    }

    public async Task ExecuteRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_contactService is null)
            throw new InvalidOperationException("Local contact persistence is unavailable.");

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is not ContactActionRequest contactRequest)
                throw new NotSupportedException($"Local contact request {request.GetType().Name} is not supported.");

            var committedContact = RequestEntityCloner.Contact(contactRequest.Contact);

            if (contactRequest.Operation == Domain.Enums.ContactSynchronizerOperation.SetPhoto)
            {
                if (_contactPictureFileService is null)
                    throw new InvalidOperationException("Local contact picture persistence is unavailable.");

                committedContact.ContactPictureFileId = await _contactPictureFileService
                    .SaveContactPictureAsync(contactRequest.Photo)
                    .ConfigureAwait(false);
            }
            else if (contactRequest.Operation == Domain.Enums.ContactSynchronizerOperation.DeletePhoto)
            {
                committedContact.ContactPictureFileId = null;
            }

            await _contactService.CompleteMutationAsync(
                contactRequest.LocalContactId,
                committedContact,
                contactRequest.Operation == Domain.Enums.ContactSynchronizerOperation.Delete).ConfigureAwait(false);
        }
    }

    public Task<ContactSynchronizationResult> SynchronizeAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ContactSynchronizationResult.Empty);
    }
}
