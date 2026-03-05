using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Migrations;

public class VNextDelayMigration : IAppMigration
{
    public string MigrationId => "vnext-delay";

    public Task ExecuteAsync() => Task.Delay(3000);
}
