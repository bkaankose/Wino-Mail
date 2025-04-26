using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests.Bundles;

namespace Wino.Core.Synchronizers.Errors.Outlook;

public class ObjectCannotBeDeletedHandler : ISynchronizerErrorHandler
{
    private readonly IMailService _mailService;

    public ObjectCannotBeDeletedHandler(IMailService mailService)
    {
        _mailService = mailService;
    }

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.ErrorMessage.Contains("ErrorCannotDeleteObject") && error.RequestBundle is HttpRequestBundle<RequestInformation>;
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
