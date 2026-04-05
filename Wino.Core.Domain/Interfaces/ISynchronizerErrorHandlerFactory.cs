using System.Threading.Tasks;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

public interface ISynchronizerErrorHandlerFactory
{
    Task<bool> HandleErrorAsync(SynchronizerErrorContext error);
}
