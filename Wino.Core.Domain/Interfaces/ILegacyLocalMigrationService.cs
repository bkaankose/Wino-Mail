using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface ILegacyLocalMigrationService
{
    Task<LegacyLocalMigrationPreview> DetectAsync(CancellationToken cancellationToken = default);
    Task<LegacyLocalMigrationResult> ImportAsync(CancellationToken cancellationToken = default);
    void MarkPromptDeferred();
}
