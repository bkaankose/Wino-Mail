using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAppInitializerService
    {
        string GetApplicationDataFolder();
        string GetPublisherSharedFolder();

        Task MigrateAsync();
    }
}
