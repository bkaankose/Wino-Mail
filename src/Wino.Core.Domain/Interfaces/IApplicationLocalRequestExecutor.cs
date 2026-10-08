using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IApplicationLocalRequestExecutor
{
    Task ExecuteAsync(IRequestBase request);
}
