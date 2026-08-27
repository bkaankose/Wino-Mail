using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Helpers;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Services;

/// <summary>
/// Commits accountless application data immediately after optimistic UI apply.
/// </summary>
public sealed class ApplicationLocalRequestExecutor : IApplicationLocalRequestExecutor
{
    private readonly IContactService _contactService;

    public ApplicationLocalRequestExecutor(IContactService contactService)
    {
        _contactService = contactService;
    }

    public async Task ExecuteAsync(IRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestUiChangeCoordinator.ApplyRequests([request]);

        try
        {
            if (request is not ApplicationLocalContactRequest contactRequest)
                throw new NotSupportedException($"Application-local request {request.GetType().Name} is not supported.");

            await CommitAsync(contactRequest).ConfigureAwait(false);
            RequestUiChangeCoordinator.CompleteRequests([request]);
        }
        catch
        {
            RequestUiChangeCoordinator.RevertRequests([request]);
            RequestUiChangeCoordinator.CompleteRequests([request]);
            throw;
        }
    }

    private Task CommitAsync(ApplicationLocalContactRequest request)
        => request.Operation switch
        {
            ApplicationLocalContactOperation.SetFavorite => _contactService.SetContactFavoriteAsync(request.Contact.Id, request.Contact.IsFavorite),
            ApplicationLocalContactOperation.CreateList => _contactService.SaveContactListAsync(request.List),
            ApplicationLocalContactOperation.UpdateList => _contactService.UpdateContactListAsync(request.List),
            ApplicationLocalContactOperation.DeleteList => _contactService.DeleteContactListAsync(request.List.Id),
            ApplicationLocalContactOperation.AddMembership => _contactService.AddContactsToListAsync(request.List.Id, request.ContactIds),
            ApplicationLocalContactOperation.RemoveMembership => _contactService.RemoveContactsFromListAsync(request.List.Id, request.ContactIds),
            ApplicationLocalContactOperation.SetMemberships => _contactService.SetListsForContactAsync(request.Contact.Id, request.DesiredListIds),
            _ => throw new NotSupportedException($"Application-local operation {request.Operation} is not supported.")
        };
}
