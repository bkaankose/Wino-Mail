using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Core.Domain.Interfaces;

public interface IMigrationAccountAuthorizationService
{
    Task AuthenticateAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default);

    Task SkipAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default);
}
