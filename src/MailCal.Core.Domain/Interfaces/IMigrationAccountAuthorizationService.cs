using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Migration;

namespace MailCal.Core.Domain.Interfaces;

public interface IMigrationAccountAuthorizationService
{
    Task AuthenticateAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default);

    Task SkipAsync(
        MigrationAccountOptions options,
        CancellationToken cancellationToken = default);
}
