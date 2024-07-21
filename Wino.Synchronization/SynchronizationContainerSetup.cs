using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Services;
using Wino.Domain.Interfaces;

namespace Wino.Synchronization
{
    public static class SynchronizationContainerSetup
    {
        public static void RegisterSynchronizationServices(this IServiceCollection services)
        {
            services.AddTransient<IImapTestService, ImapTestService>();
        }
    }
}
