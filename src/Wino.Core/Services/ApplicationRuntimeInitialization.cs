using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Services;

/// <summary>Checkpoints completed startup steps so retry does not repeat subscriptions.</summary>
public sealed class ApplicationRuntimeInitialization(
    Func<IDatabaseService> database,
    Func<IApplicationConfiguration> configuration,
    Func<ITranslationService> translations,
    Func<SynchronizationManagerInitializer> synchronizationInitializer,
    Func<IKeyboardShortcutService> shortcuts,
    Func<AccountProfilePictureMaintenance> pictures,
    Func<AccountSenderPictureDirectory> senderPictures)
{
    // Local store of the removed cloud intelligence feature, left behind by upstream Wino Mail installs.
    private static readonly string[] LegacyIntelligenceFiles =
        ["WinoMailIntelligence.db", "WinoMailIntelligence.db-wal", "WinoMailIntelligence.db-shm"];

    private int _completedStep;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StepAsync(1, () => translations().InitializeAsync(), cancellationToken);
        await StepAsync(2, () => Task.WhenAll(database().InitializeAsync(),
            translations().InitializeAsync(), synchronizationInitializer().InitializeAsync()), cancellationToken);
        await StepAsync(3, () => shortcuts().InitializeAsync(), cancellationToken);
        await StepAsync(4, () => pictures().MigrateLegacyAsync(), cancellationToken);
        await StepAsync(5, () => senderPictures().InitializeAsync(), cancellationToken);
    }

    public Task RunBackgroundStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteLegacyIntelligenceStore();
        return pictures().BackfillAsync();
    }

    private void DeleteLegacyIntelligenceStore()
    {
        var folder = configuration().ApplicationDataFolderPath;
        if (string.IsNullOrEmpty(folder))
            return;

        foreach (var fileName in LegacyIntelligenceFiles)
        {
            try
            {
                var path = Path.Combine(folder, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Legacy intelligence store file {FileName} could not be removed.", fileName);
            }
        }
    }

    private async Task StepAsync(int step, Func<Task> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_completedStep >= step)
            return;

        await operation();
        _completedStep = step;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
