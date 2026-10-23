using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Helpers;
using MailCal.Core.Requests.Contact;

namespace MailCal.Core.Services;

/// <summary>
/// Commits application data immediately after optimistic UI apply. It only handles data
/// no provider knows about, so nothing ever leaves the device here.
/// </summary>
public sealed class ApplicationLocalRequestExecutor : IApplicationLocalRequestExecutor
{
    private readonly IContactService _contactService;

    public ApplicationLocalRequestExecutor(IContactService contactService)
        => _contactService = contactService;

    public async Task ExecuteAsync(IRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestUiChangeCoordinator.ApplyRequests([request]);

        try
        {
            if (request is not ApplicationLocalContactRequest contactRequest)
                throw new NotSupportedException($"Application-local request {request.GetType().Name} is not supported.");

            await _contactService.SetContactFavoriteAsync(contactRequest.Contact.Id, contactRequest.Contact.IsFavorite).ConfigureAwait(false);
            RequestUiChangeCoordinator.CompleteRequests([request]);
        }
        catch
        {
            RequestUiChangeCoordinator.RevertRequests([request]);
            RequestUiChangeCoordinator.CompleteRequests([request]);
            throw;
        }
    }
}
