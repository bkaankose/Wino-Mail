using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Core.Requests.Bundles;

namespace MailCal.Core.Synchronizers.Errors.Outlook;

public class ObjectCannotBeDeletedHandler : ISynchronizerErrorHandler
{
    private readonly IMailService _mailService;

    public ObjectCannotBeDeletedHandler(IMailService mailService)
    {
        _mailService = mailService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.ErrorMessage.Contains("Object cannot be deleted.") && error.RequestBundle is HttpRequestBundle<RequestInformation>;
    }

    public async Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        var castedBundle = error.RequestBundle as HttpRequestBundle<RequestInformation>;

        if (castedBundle?.Request is MailRequestBase mailRequest)
        {
            var request = castedBundle.Request;

            await _mailService.DeleteMailAsync(error.Account.Id, mailRequest.Item.Id);

            return true;
        }

        return false;
    }
}
