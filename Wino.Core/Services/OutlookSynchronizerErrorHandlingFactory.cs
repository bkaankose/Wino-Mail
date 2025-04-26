using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;
using Wino.Core.Synchronizers.Errors.Outlook;

namespace Wino.Core.Services;

public class OutlookSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IOutlookSynchronizerErrorHandlerFactory
{
    public OutlookSynchronizerErrorHandlingFactory(ObjectCannotBeDeletedHandler objectCannotBeDeleted)
    {
        RegisterHandler(objectCannotBeDeleted);
    }

    public bool CanHandle(SynchronizerErrorContext error) => CanHandle(error);

    public Task HandleAsync(SynchronizerErrorContext error) => HandleErrorAsync(error);
}
