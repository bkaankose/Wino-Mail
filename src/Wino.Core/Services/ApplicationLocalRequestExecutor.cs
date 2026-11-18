using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Helpers;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Services;

/// <summary>
/// Commits application data immediately after optimistic UI apply. It only handles data
/// no provider knows about, so nothing ever leaves the device here.
/// </summary>
public sealed class ApplicationLocalRequestExecutor : IApplicationLocalRequestExecutor
{
    private readonly IContactService _contactService;
    private readonly RequestUiChangeCoordinator _uiChanges;

    public ApplicationLocalRequestExecutor(IContactService contactService, RequestUiChangeCoordinator uiChanges)
    {
        _contactService = contactService;
        _uiChanges = uiChanges;
    }

    public async Task ExecuteAsync(IRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _uiChanges.ApplyRequests([request]);

        try
        {
            if (request is not ApplicationLocalContactRequest contactRequest)
                throw new NotSupportedException($"Application-local request {request.GetType().Name} is not supported.");

            await _contactService.SetContactFavoriteAsync(contactRequest.Contact.Id, contactRequest.Contact.IsFavorite).ConfigureAwait(false);
            _uiChanges.CompleteRequests([request]);
        }
        catch
        {
            _uiChanges.RevertRequests([request]);
            _uiChanges.CompleteRequests([request]);
            throw;
        }
    }
}
