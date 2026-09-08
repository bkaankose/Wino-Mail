using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Extensions;

namespace Wino.Core.Synchronizers.Mail;

public partial class OutlookSynchronizer
{
    private sealed record AttachmentFingerprint(string Name, string ContentType, bool Inline, string ContentId, string Hash);
    private sealed record CachedAttachment(DateTimeOffset? Modified, AttachmentFingerprint Fingerprint);
    private readonly ConcurrentDictionary<(string Message, string Attachment), CachedAttachment> _draftAttachmentCache = new();

    private static AttachmentFingerprint Fingerprint(FileAttachment file) => new(file.Name, file.ContentType,
        file.IsInline ?? false, file.ContentId ?? string.Empty, Convert.ToHexString(SHA256.HashData(file.ContentBytes ?? [])));

    internal static Message CreateDraftPatch(MimeMessage mime)
    {
        var message = mime.AsOutlookMessage(false);
        message.IsDraft = null;
        message.IsRead = null;
        message.ConversationId = null;
        message.InternetMessageHeaders = null;
        message.Subject ??= string.Empty;
        return message;
    }

    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot,
        MailCopy draft, CancellationToken cancellationToken = default)
    {
        using var mime = snapshot.OpenMime();
        await _graphClient.Me.Messages[draft.Id].PatchAsync(CreateDraftPatch(mime), cancellationToken: cancellationToken).ConfigureAwait(false);
        await ReconcileDraftAttachmentsAsync(draft.Id, mime, cancellationToken).ConfigureAwait(false);
        return DraftUpdateIdentity.From(draft);
    }

    internal static bool SameDraftAttachment(FileAttachment left, FileAttachment right) =>
        left.ContentBytes != null && right.ContentBytes != null && Fingerprint(left) == Fingerprint(right);

    private async Task ReconcileDraftAttachmentsAsync(string messageId, MimeMessage mime, CancellationToken token)
    {
        var remaining = mime.ExtractAttachments();
        var existing = new List<Attachment>();
        var page = await _graphClient.Me.Messages[messageId].Attachments.GetAsync(
            options => options.QueryParameters.Select = ["id", "name", "contentType", "size", "isInline", "lastModifiedDateTime"], cancellationToken: token).ConfigureAwait(false);
        while (page != null)
        {
            existing.AddRange(page.Value ?? []);
            if (string.IsNullOrEmpty(page.OdataNextLink)) break;
            page = await _graphClient.Me.Messages[messageId].Attachments.WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: token).ConfigureAwait(false);
        }

        foreach (var attachment in existing)
        {
            token.ThrowIfCancellationRequested();
            var key = (messageId, attachment.Id);
            AttachmentFingerprint fingerprint = null;
            if (_draftAttachmentCache.TryGetValue(key, out var cached) && cached.Modified == attachment.LastModifiedDateTime)
                fingerprint = cached.Fingerprint;
            else
            {
                var full = attachment as FileAttachment;
                if (full != null && full.ContentBytes == null)
                    full = await _graphClient.Me.Messages[messageId].Attachments[attachment.Id]
                        .GetAsync(cancellationToken: token).ConfigureAwait(false) as FileAttachment;
                if (full?.ContentBytes != null)
                {
                    fingerprint = Fingerprint(full);
                    _draftAttachmentCache[key] = new(attachment.LastModifiedDateTime, fingerprint);
                }
            }

            var match = fingerprint == null ? null : remaining.FirstOrDefault(x => Fingerprint(x) == fingerprint);
            if (match != null) remaining.Remove(match);
            else
            {
                await _graphClient.Me.Messages[messageId].Attachments[attachment.Id].DeleteAsync(cancellationToken: token).ConfigureAwait(false);
                _draftAttachmentCache.TryRemove(key, out _);
            }
        }

        foreach (var attachment in remaining)
            await UploadDraftAttachmentAsync(messageId, attachment, token).ConfigureAwait(false);
    }

    private async Task UploadDraftAttachmentAsync(string messageId, FileAttachment attachment, CancellationToken token)
    {
        var bytes = attachment.ContentBytes ?? [];
        if (bytes.Length < SimpleAttachmentUploadLimitBytes)
        {
            await _graphClient.Me.Messages[messageId].Attachments.PostAsync(attachment, cancellationToken: token).ConfigureAwait(false);
            return;
        }
        if (bytes.Length > MaximumUploadSessionAttachmentSizeBytes)
            throw new InvalidOperationException("Draft attachment exceeds the provider limit.");

        var body = new Microsoft.Graph.Me.Messages.Item.Attachments.CreateUploadSession.CreateUploadSessionPostRequestBody
        {
            AttachmentItem = new AttachmentItem
            {
                AttachmentType = AttachmentType.File, Name = attachment.Name, Size = bytes.LongLength,
                ContentType = attachment.ContentType, ContentId = attachment.ContentId, IsInline = attachment.IsInline
            }
        };
        var session = await _graphClient.Me.Messages[messageId].Attachments.CreateUploadSession
            .PostAsync(body, cancellationToken: token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(session?.UploadUrl)) throw new InvalidOperationException("Draft upload session is unavailable.");

        try { await UploadAttachmentInChunksAsync(session.UploadUrl, bytes, token).ConfigureAwait(false); }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient();
            try
            {
                using var response = await client.DeleteAsync(session.UploadUrl, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* A session also expires on the server. Never log its URL. */ }
            throw;
        }
    }
}
