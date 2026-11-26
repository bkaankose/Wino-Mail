using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// Sender photos from the organization's directory, as the EWS synchronizer stores them, read with ResolveRecipients
/// and its Picture option ([MS-ASCMD] 2.2.1.15, 2.2.3.135.2; 14.1 and later). One address per request, outside the
/// engine's synchronization gate.
/// </summary>
internal sealed partial class ActiveSyncExchangeSynchronizer
{
    private const int SenderPhotoLookupBatchSize = 25;
    private static readonly TimeSpan SenderPhotoLookupInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SenderPhotoLookupSpacing = TimeSpan.FromMilliseconds(500);

    private int _isSenderPhotoLookupRunning;
    private DateTime _lastSenderPhotoLookupUtc;

    protected override void OnMailSynchronizationCompleted(MailSynchronizationResult result)
    {
        if (result.CompletedState != SynchronizationCompletedState.Success)
            return;

        var hasNewMail = result.DownloadedMessages?.Any() == true;
        if (!hasNewMail && DateTime.UtcNow - _lastSenderPhotoLookupUtc < SenderPhotoLookupInterval)
            return;

        if (Interlocked.Exchange(ref _isSenderPhotoLookupRunning, 1) != 0)
            return;

        _lastSenderPhotoLookupUtc = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                await LookUpSenderPhotosAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "ActiveSync sender photo lookup for {Account} stopped.", Account.Name);
            }
            finally
            {
                Volatile.Write(ref _isSenderPhotoLookupRunning, 0);
            }
        });
    }

    /// <summary>
    /// Looks up a batch of senders from the account's own domain that were never looked up and have no picture. Every
    /// answer is stored, no photo included, so no address is asked about twice: an address the directory does not
    /// resolve, Picture status 173 (no photo) and 174 (larger than the size asked for) store no photo. A failed request
    /// ends the batch without recording anything; the next pass asks again.
    /// </summary>
    private async Task LookUpSenderPhotosAsync()
    {
        var domain = AddressDomain(Account.Address);
        if (domain is null)
            return;

        if (Version.Parse(await _engine.GetProtocolVersionAsync(CancellationToken.None).ConfigureAwait(false)) < new Version(14, 1))
            return;

        var candidates = await _contacts.GetDirectoryPhotoCandidatesAsync(Account.Id, domain, SenderPhotoLookupBatchSize).ConfigureAwait(false);
        var found = 0;
        foreach (var address in candidates)
        {
            ProfilePictureFetchResult? picture;
            try
            {
                var recipient = await _engine.ResolveRecipientPictureAsync(address, ProfilePictureMaximumBytes, CancellationToken.None).ConfigureAwait(false);
                picture = recipient == null ? ProfilePictureFetchResult.ConfirmedAbsent : EasUserInformation.ReadPicture(recipient);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or AuthenticationAttentionException))
            {
                Logger.Debug(exception, "The ActiveSync directory photo of {Address} could not be read.", address);
                break;
            }

            var photo = picture?.Status == ProfilePictureFetchStatus.Downloaded ? picture.ImageData : null;
            await _contacts.SaveDirectoryPhotoLookupAsync(Account.Id, address, photo).ConfigureAwait(false);
            if (photo != null)
                found++;

            await Task.Delay(SenderPhotoLookupSpacing).ConfigureAwait(false);
        }

        if (candidates.Count > 0)
            Logger.Information("ActiveSync sender photos for {Account}: {Found} of {Count} senders have a directory photo.", Account.Name, found, candidates.Count);
    }

    private static string? AddressDomain(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return address != null && at > 0 && at < address.Length - 1 ? address[(at + 1)..].Trim() : null;
    }
}
