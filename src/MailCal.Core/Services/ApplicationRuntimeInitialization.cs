using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Interfaces;
using MailCal.Services;

namespace MailCal.Core.Services;

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
    // Data of removed cloud features left behind by earlier builds, under their original names: the
    // intelligence store and the Gravatar/favicon image cache.
    private static readonly string[] LegacyFiles =
        ["WinoMailIntelligence.db", "WinoMailIntelligence.db-wal", "WinoMailIntelligence.db-shm"];
    private static readonly string[] LegacyFolders = ["thumbnails"];

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
        DeleteLegacyCloudData();
        return pictures().BackfillAsync();
    }

    private void DeleteLegacyCloudData()
    {
        var folder = configuration().ApplicationDataFolderPath;
        if (string.IsNullOrEmpty(folder))
            return;

        foreach (var fileName in LegacyFiles)
        {
            try
            {
                var path = Path.Combine(folder, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Legacy file {FileName} could not be removed.", fileName);
            }
        }

        foreach (var folderName in LegacyFolders)
        {
            try
            {
                var path = Path.Combine(folder, folderName);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Legacy folder {FolderName} could not be removed.", folderName);
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
