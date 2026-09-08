using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>Retained for compatibility; publisher data belongs to legacy installations.</summary>
public sealed class ReleaseLocalAccountDataCleanupService
{
    public ReleaseLocalAccountDataCleanupService(IConfigurationService configurationService,
        IApplicationConfiguration applicationConfiguration)
    {
    }

    public Task RunIfNeededAsync() => Task.CompletedTask;
}
