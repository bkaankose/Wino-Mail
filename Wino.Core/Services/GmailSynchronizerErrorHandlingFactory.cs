using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;

namespace Wino.Core.Services;
public class GmailSynchronizerErrorHandlingFactory : SynchronizerErrorHandlingFactory, IGmailSynchronizerErrorHandlerFactory
{
    public bool CanHandle(SynchronizerErrorContext error) => CanHandle(error);

    public Task HandleAsync(SynchronizerErrorContext error) => HandleErrorAsync(error);
}
