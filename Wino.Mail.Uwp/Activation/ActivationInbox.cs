using System.Text.Json;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Wino.Mail.Uwp.Activation;

public sealed class ActivationInbox
{
    private const string FolderName = "ActivationInbox";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ActivationEnvelope> EnqueueAsync(
        IActivatedEventArgs activationArgs,
        ActivationTargetSurface targetSurface)
    {
        ArgumentNullException.ThrowIfNull(activationArgs);

        var (kind, arguments, storageTokens) = await SerializeActivationAsync(activationArgs);
        var envelope = new ActivationEnvelope(
            1,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.UtcTicks,
            targetSurface,
            kind,
            arguments,
            storageTokens,
            ActivationDeliveryState.Pending);

        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
        var temporaryFile = await folder.CreateFileAsync($"{envelope.Id:N}.new", CreationCollisionOption.FailIfExists);
        await FileIO.WriteTextAsync(temporaryFile, JsonSerializer.Serialize(envelope, JsonOptions));
        await temporaryFile.RenameAsync($"{envelope.Id:N}.json", NameCollisionOption.FailIfExists);
        return envelope;
    }

    public async Task<IReadOnlyList<ActivationEnvelope>> ClaimPendingAsync()
    {
        await gate.WaitAsync();
        try
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
            var files = (await folder.GetFilesAsync())
                .Where(file => string.Equals(file.FileType, ".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.DateCreated)
                .ToArray();
            var claimed = new List<ActivationEnvelope>(files.Length);
            var seenIds = new HashSet<Guid>();

            foreach (var file in files)
            {
                ActivationEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<ActivationEnvelope>(await FileIO.ReadTextAsync(file), JsonOptions);
                }
                catch (JsonException)
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    continue;
                }

                if (envelope is null || envelope.IsExpired(DateTimeOffset.UtcNow) || !seenIds.Add(envelope.Id))
                {
                    await DeleteEnvelopeAsync(file, envelope);
                    continue;
                }

                var processing = envelope with
                {
                    DeliveryState = ActivationDeliveryState.Processing,
                    DeliveryAttempt = envelope.DeliveryAttempt + 1,
                };
                await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(processing, JsonOptions));
                claimed.Add(processing);
            }

            return claimed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(ActivationEnvelope envelope)
    {
        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
        var item = await folder.TryGetItemAsync($"{envelope.Id:N}.json");
        if (item is StorageFile file)
        {
            await DeleteEnvelopeAsync(file, envelope);
        }
    }

    private static async Task<(WinoActivationKind Kind, string Arguments, string[] Tokens)> SerializeActivationAsync(
        IActivatedEventArgs activationArgs)
    {
        switch (activationArgs)
        {
            case ProtocolActivatedEventArgs protocol:
                return (WinoActivationKind.Protocol, protocol.Uri.ToString(), []);
            case FileActivatedEventArgs fileActivation:
            {
                var tokens = new List<string>(fileActivation.Files.Count);
                foreach (var item in fileActivation.Files.OfType<StorageFile>())
                {
                    tokens.Add(SharedStorageAccessManager.AddFile(item));
                }

                return (WinoActivationKind.File, string.Empty, tokens.ToArray());
            }
            case ToastNotificationActivatedEventArgs toast:
                return (WinoActivationKind.Toast, toast.Argument ?? string.Empty, []);
            case ShareTargetActivatedEventArgs share:
            {
                share.ShareOperation.ReportStarted();
                var data = share.ShareOperation.Data;
                var tokens = new List<string>();
                if (data.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await data.GetStorageItemsAsync();
                    foreach (var item in items.OfType<StorageFile>())
                    {
                        tokens.Add(SharedStorageAccessManager.AddFile(item));
                    }
                }

                var text = data.Contains(StandardDataFormats.Text)
                    ? await data.GetTextAsync()
                    : string.Empty;
                var webLink = data.Contains(StandardDataFormats.WebLink)
                    ? (await data.GetWebLinkAsync()).ToString()
                    : null;
                var payload = new ShareActivationPayload(data.Properties.Title ?? string.Empty, text, webLink);
                return (WinoActivationKind.Share, JsonSerializer.Serialize(payload, JsonOptions), tokens.ToArray());
            }
            case LaunchActivatedEventArgs launch:
                return (WinoActivationKind.Launch, launch.Arguments ?? string.Empty, []);
            default:
                return (WinoActivationKind.Launch, string.Empty, []);
        }
    }

    private static async Task DeleteEnvelopeAsync(StorageFile file, ActivationEnvelope? envelope)
    {
        if (envelope is not null)
        {
            foreach (var token in envelope.SharedStorageTokens)
            {
                SharedStorageAccessManager.RemoveFile(token);
            }
        }

        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
    }
}
