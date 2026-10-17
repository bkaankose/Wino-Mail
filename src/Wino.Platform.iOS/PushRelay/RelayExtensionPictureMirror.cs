#nullable enable
using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;
using Wino.Platform.Apple.PushRelay;

namespace Wino.Platform.iOS.PushRelay;

/// <summary>
/// Copies every synced contact picture into the App Group folder the Notification Service Extension reads, one file
/// per email address (<see cref="RelayExtensionShared.SharedPictureFileName"/>). Runs when services are ready and,
/// debounced, after each account synchronization, so a new contact photo is there before the next relay alert.
/// </summary>
public sealed class RelayExtensionPictureMirror(IContactService contacts, IPictureStorageService pictures, IMessenger messenger)
    : IRecipient<AccountSynchronizationCompleted>
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<RelayExtensionPictureMirror>();
    private CancellationTokenSource? _pending;
    private bool _started;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            messenger.Register<AccountSynchronizationCompleted>(this);
            await MirrorAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "The contact picture mirror could not start.");
        }
    }

    public void Receive(AccountSynchronizationCompleted message)
    {
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, next)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, next.Token).ConfigureAwait(false);
                await MirrorAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private static string? GroupRoot()
    {
        using var container = NSFileManager.DefaultManager.GetContainerUrl(RelayExtensionShared.AppGroupIdentifier);
        return container?.Path is { } root ? Path.Combine(root, RelayExtensionShared.SharedPicturesFolder) : null;
    }

    private async Task MirrorAsync()
    {
        if (!await _lock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (GroupRoot() is not { } folder)
            {
                _logger.Information("No App Group container; contact pictures are not shared with the notification extension.");
                return;
            }
            Directory.CreateDirectory(folder);

            var copied = 0;
            var withPicture = 0;
            var books = await contacts.GetAddressBooksAsync().ConfigureAwait(false) ?? [];
            foreach (var book in books)
            {
                var list = await contacts.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false) ?? [];
                foreach (var contact in list)
                {
                    if (contact.ContactPictureFileId is not { } fileId) continue;
                    withPicture++;
                    var source = pictures.GetPicturePath(PictureKind.Contact, fileId);
                    if (source is null || !File.Exists(source)) continue;
                    var sourceTime = File.GetLastWriteTimeUtc(source);
                    foreach (var email in contact.EmailAddresses ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(email.Address) || !email.Address.Contains('@')) continue;
                        var target = Path.Combine(folder, RelayExtensionShared.SharedPictureFileName(email.Address));
                        if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= sourceTime) continue;
                        File.Copy(source, target, overwrite: true);
                        File.SetLastWriteTimeUtc(target, sourceTime);
                        copied++;
                    }
                }
            }

            _logger.Information("Contact picture mirror: {Books} address books, {WithPicture} contacts with pictures, {Copied} copied to {Folder}.",
                books.Count, withPicture, copied, folder);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Contact pictures could not be shared with the notification extension.");
        }
        finally
        {
            _lock.Release();
        }
    }
}
