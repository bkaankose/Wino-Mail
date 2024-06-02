using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ConsoleTest.Services
{
    internal class ConsoleAppInitializerService : IAppInitializerService
    {
        public string GetApplicationDataFolder() => "C:\\Users\\bkaan\\Desktop\\WinoTest";

        public Task MigrateAsync() => Task.CompletedTask;
    }
}
