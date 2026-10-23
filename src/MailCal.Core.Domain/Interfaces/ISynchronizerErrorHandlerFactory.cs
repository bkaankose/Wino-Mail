using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Core.Domain.Interfaces;

public interface ISynchronizerErrorHandlerFactory
{
    Task<bool> HandleErrorAsync(SynchronizerErrorContext error);
}
