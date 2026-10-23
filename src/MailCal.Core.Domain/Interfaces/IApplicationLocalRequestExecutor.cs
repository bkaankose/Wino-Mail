using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

public interface IApplicationLocalRequestExecutor
{
    Task ExecuteAsync(IRequestBase request);
}
