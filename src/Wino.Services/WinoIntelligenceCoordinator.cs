#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed partial class WinoIntelligenceCoordinator : IWinoIntelligenceCoordinator, IDisposable
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ISemanticIndexCoordinator _semanticIndexCoordinator;
    private readonly IIntelligenceMessageContextResolver _messageResolver;
    private readonly ILocalIntelligenceStore _localStore;
    private readonly IMimeFileService _mimeFileService;
    private readonly IMailService _mailService;
    private readonly IAccountService _accountService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly ITranslationService _translationService;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoLogger _logger;
    private readonly ILocalIntelligenceSearchEngine _localSearch;
    private readonly IMailContentProjector _contentProjector;
    private readonly IWinoAccountIntelligenceSnapshotService? _accountSnapshotService;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _requests = new();

    public WinoIntelligenceCoordinator(
        IWinoAccountProfileService profileService,
        IWinoAccountApiClient apiClient,
        ISemanticIndexCoordinator semanticIndexCoordinator,
        IIntelligenceMessageContextResolver messageResolver,
        ILocalIntelligenceStore localStore,
        IMimeFileService mimeFileService,
        IMailService mailService,
        IAccountService accountService,
        IWinoRequestDelegator requestDelegator,
        ITranslationService translationService,
        IPreferencesService preferencesService,
        IWinoLogger logger,
        ILocalIntelligenceSearchEngine localSearch,
        IMailContentProjector contentProjector,
        IWinoAccountIntelligenceSnapshotService? accountSnapshotService = null)
    {
        _profileService = profileService;
        _apiClient = apiClient;
        _semanticIndexCoordinator = semanticIndexCoordinator;
        _messageResolver = messageResolver;
        _localStore = localStore;
        _mimeFileService = mimeFileService;
        _mailService = mailService;
        _accountService = accountService;
        _requestDelegator = requestDelegator;
        _translationService = translationService;
        _preferencesService = preferencesService;
        _logger = logger;
        _localSearch = localSearch;
        _contentProjector = contentProjector;
        _accountSnapshotService = accountSnapshotService;

        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this, static (recipient, _) =>
            ((WinoIntelligenceCoordinator)recipient).InvalidateAccess());
        WeakReferenceMessenger.Default.Register<WinoAccountSignedInMessage>(this, static (recipient, _) =>
            ((WinoIntelligenceCoordinator)recipient).InvalidateAccessAndNotify());
        WeakReferenceMessenger.Default.Register<WinoAccountSignedOutMessage>(this, static (recipient, _) =>
            ((WinoIntelligenceCoordinator)recipient).InvalidateAccessAndNotify());
        WeakReferenceMessenger.Default.Register<WinoAccountProfileUpdatedMessage>(this, static (recipient, _) =>
            ((WinoIntelligenceCoordinator)recipient).InvalidateAccessAndNotify());
    }

    public async Task<WinoIntelligenceSnapshot> GetSnapshotAsync(
        WinoIntelligenceContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = context.IntelligenceMetadata;
            var needsReply = metadata?.NeedsReply?.Value == true;
            var needsReplyDetail = metadata?.Headline ?? string.Empty;
            var deadlinePayload = metadata?.Deadline;
            var deadline = deadlinePayload?.HasDeadline == true
                ? new WinoIntelligenceDeadline(
                    deadlinePayload.Action,
                    deadlinePayload.DueAtUtc,
                    deadlinePayload.LocalDate,
                    deadlinePayload.LocalDateEnd,
                    deadlinePayload.TimeZoneId,
                    deadlinePayload.Precision,
                    deadlinePayload.Confidence)
                : null;

            AccessSnapshot access;
            try
            {
                access = await GetAccessAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.CaptureException(exception, "LoadWinoIntelligenceAccess");
                access = AccessSnapshot.None;
            }

            if (!access.HasAiPack)
            {
                return WinoIntelligenceSnapshot.Hidden;
            }

            var isSupportedProvider = context.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.POP3;
            var candidate = access.HasIntelligenceConsent && isSupportedProvider
                ? await _messageResolver.FindCandidateAsync(context.LocalAccountId, context.MessageId, cancellationToken).ConfigureAwait(false)
                : null;
            var processingAvailable = access.HasIntelligenceConsent &&
                                      context.IsSemanticIndexingEnabled &&
                                      candidate is not null &&
                                      isSupportedProvider;
            var state = processingAvailable
                ? await _semanticIndexCoordinator.GetMessageStateAsync(context.LocalAccountId, context.MessageId, cancellationToken).ConfigureAwait(false)
                : SemanticMessageIndexState.Unsupported;

            var language = ResolveSummaryLanguage();
            var inference = context.InferenceProjection ??
                            _contentProjector.Project(context.Html, MailContentProjectionProfile.Inference).Projection;
            var cachedSummary = access.HasIntelligenceConsent
                ? await _mimeFileService.GetSummaryTextAsync(
                    context.LocalAccountId,
                    context.FileId,
                    CreateSummaryCacheKey(inference, language),
                    cancellationToken).ConfigureAwait(false)
                : string.Empty;
            // The header is the entry point for consent, processing, and on-demand actions. An
            // account that owns the add-on must retain that entry point even before consent or
            // semantic indexing is enabled.
            var visible = access.HasAiPack;
            return new WinoIntelligenceSnapshot(
                visible,
                access.HasIntelligenceConsent,
                access.HasIntelligenceConsent,
                processingAvailable,
                processingAvailable,
                processingAvailable && state == SemanticMessageIndexState.Indexed,
                state,
                access.MailboxId,
                candidate?.RemoteMessageId,
                needsReply,
                needsReplyDetail,
                deadline,
                string.IsNullOrWhiteSpace(cachedSummary) ? null : cachedSummary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "LoadWinoIntelligenceSnapshot");
            return WinoIntelligenceSnapshot.Hidden;
        }
    }

    public async Task RequestProcessingAsync(WinoIntelligenceContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsProcessingAvailable)
            throw new InvalidOperationException(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
        await _semanticIndexCoordinator.IndexMessageAsync(context.LocalAccountId, context.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Summaries follow the language chosen in Wino Intelligence settings. When the user has not
    /// chosen one, they follow the app display language.
    /// </summary>
    private string ResolveSummaryLanguage()
    {
        var preferredLanguage = _preferencesService.AiSummarizeLanguageCode;

        return !string.IsNullOrWhiteSpace(preferredLanguage)
            ? preferredLanguage
            : _translationService.CurrentLanguageModel?.Code ?? "en-US";
    }

    public Task<WinoIntelligenceOperationResult<string>> SummarizeAsync(
        WinoIntelligenceContext context,
        Guid requestId,
        CancellationToken cancellationToken = default)
        => RunAsync(context, requestId, async token =>
        {
            var snapshot = await GetSnapshotAsync(context, token).ConfigureAwait(false);
            if (!snapshot.IsSummaryAvailable)
                throw new InvalidOperationException(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
            var language = ResolveSummaryLanguage();
            var projection = context.InferenceProjection ??
                             _contentProjector.Project(context.Html, MailContentProjectionProfile.Inference).Projection;
            var cacheKey = CreateSummaryCacheKey(projection, language);
            var cached = await _mimeFileService.GetSummaryTextAsync(context.LocalAccountId, context.FileId, cacheKey, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached;
            var summarySegments = CreateSummarySegments(projection.Segments);
            if (summarySegments.Count == 0)
                throw new InvalidOperationException(ApiErrorCodes.AiHtmlEmpty);
            var response = await _profileService.SummarizeAsync(summarySegments, language, token).ConfigureAwait(false);
            var summary = RequireSummary(response, "Summary request failed.");
            await _mimeFileService.SaveSummaryTextAsync(context.LocalAccountId, context.FileId, cacheKey, summary, token).ConfigureAwait(false);
            return summary;
        }, cancellationToken);

    public Task<WinoIntelligenceOperationResult<MailTranslationResult>> TranslateAsync(
        WinoIntelligenceContext context,
        Guid requestId,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
        => RunAsync(context, requestId, async token =>
        {
            var snapshot = await GetSnapshotAsync(context, token).ConfigureAwait(false);
            if (!snapshot.IsTranslateAvailable)
                throw new InvalidOperationException(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
            var projection = context.TranslationProjection ??
                             _contentProjector.Project(context.Html, MailContentProjectionProfile.Translation).Projection;
            var cacheKey = CreateTranslationCacheKey(projection, sourceLanguage, targetLanguage);
            var cached = await _mimeFileService.GetTranslationMapJsonAsync(context.LocalAccountId, context.FileId, cacheKey, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var cachedResult = JsonSerializer.Deserialize(cached, WinoIntelligenceJsonContext.Default.MailTranslationResult);
                if (cachedResult is not null)
                    return cachedResult;
            }
            var response = await _profileService.TranslateAsync(projection.Segments, sourceLanguage, targetLanguage, token).ConfigureAwait(false);
            var translated = RequireTranslation(response, "Translation request failed.");
            await _mimeFileService.SaveTranslationMapJsonAsync(
                context.LocalAccountId,
                context.FileId,
                cacheKey,
                JsonSerializer.Serialize(translated, WinoIntelligenceJsonContext.Default.MailTranslationResult),
                token).ConfigureAwait(false);
            return translated;
        }, cancellationToken);

    public Task<WinoIntelligenceOperationResult<IReadOnlyList<WinoSuggestedReply>>> GetSuggestedRepliesAsync(
        WinoIntelligenceContext context,
        Guid requestId,
        CancellationToken cancellationToken = default)
        => RunAsync(context, requestId, async token =>
        {
            var snapshot = await GetSnapshotAsync(context, token).ConfigureAwait(false);
            if (!snapshot.IsSuggestedRepliesAvailable || snapshot.MailboxId is null)
                throw new InvalidOperationException(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
            var target = await _messageResolver.FindCandidateAsync(context.LocalAccountId, context.MessageId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("This message is not available for intelligence.");
            var candidates = await _messageResolver.GetCandidatesAsync(context.LocalAccountId, null, token).ConfigureAwait(false);
            var processor = CreateProcessor();
            var targetPrepared = processor.Prepare(
                context.Sender,
                context.Subject,
                new MailBodyContent(MailBodyFormat.Html, context.Html),
                EmbeddingProfile.OpenAiTextEmbedding3Small768);
            var targetMessage = ToReplyMessage(target, targetPrepared);
            var threadCandidates = string.IsNullOrWhiteSpace(target.ThreadId)
                ? []
                : candidates.Where(x => x.RemoteMessageId != target.RemoteMessageId && x.ThreadId == target.ThreadId)
                    .OrderBy(x => x.ReceivedAt).Take(12).ToArray();
            var thread = await PrepareMessagesAsync(context.LocalAccountId, threadCandidates, processor, token).ConfigureAwait(false);
            var excluded = threadCandidates.Select(static candidate => candidate.RemoteMessageId)
                .Append(target.RemoteMessageId)
                .ToHashSet(StringComparer.Ordinal);
            var similarExamples = await _localSearch.FindSimilarAsync(
                context.LocalAccountId, target.RemoteMessageId, 5, true, excluded, token).ConfigureAwait(false);
            var exampleIds = similarExamples.Select(static match => match.RemoteMessageId).ToHashSet(StringComparer.Ordinal);
            var exampleRanks = similarExamples.Select((match, index) => (match.RemoteMessageId, index))
                .ToDictionary(static pair => pair.RemoteMessageId, static pair => pair.index, StringComparer.Ordinal);
            var exampleCandidates = candidates.Where(candidate => exampleIds.Contains(candidate.RemoteMessageId))
                .OrderBy(candidate => exampleRanks[candidate.RemoteMessageId])
                .ToArray();
            if (exampleCandidates.Length == 0)
            {
                exampleCandidates = candidates
                    .Where(candidate => candidate.IsOutgoing && !excluded.Contains(candidate.RemoteMessageId))
                    .OrderByDescending(static candidate => candidate.ReceivedAt)
                    .Take(5)
                    .ToArray();
            }
            var examples = await PrepareMessagesAsync(context.LocalAccountId, exampleCandidates, processor, token).ConfigureAwait(false);
            var result = await _apiClient.GetSuggestedRepliesAsync(
                snapshot.MailboxId.Value,
                new WinoSuggestedRepliesRequest(targetMessage, thread, examples),
                requestId,
                token).ConfigureAwait(false);
            if (result.RequestId != requestId)
                throw new InvalidOperationException("Suggested reply response did not match the active request.");
            return (IReadOnlyList<WinoSuggestedReply>)result.Suggestions;
        }, cancellationToken);

    public Task<WinoIntelligenceOperationResult<IReadOnlyList<WinoSimilarMailItem>>> FindSimilarAsync(
        WinoIntelligenceContext context,
        Guid requestId,
        CancellationToken cancellationToken = default)
        => RunAsync(context, requestId, async token =>
        {
            var snapshot = await GetSnapshotAsync(context, token).ConfigureAwait(false);
            if (!snapshot.IsFindSimilarAvailable || snapshot.MailboxId is null)
                throw new InvalidOperationException(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
            var candidates = await _messageResolver.GetCandidatesAsync(context.LocalAccountId, null, token).ConfigureAwait(false);
            var byRemoteId = candidates.GroupBy(x => x.RemoteMessageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var currentRemoteId = candidates.FirstOrDefault(candidate => candidate.UniqueId == context.MailUniqueId)?.RemoteMessageId;
            if (string.IsNullOrWhiteSpace(currentRemoteId))
                return Array.Empty<WinoSimilarMailItem>();
            var matches = await _localSearch.FindSimilarAsync(
                context.LocalAccountId, currentRemoteId, 10, false, null, token).ConfigureAwait(false);
            return (IReadOnlyList<WinoSimilarMailItem>)matches
                .Where(x => byRemoteId.ContainsKey(x.RemoteMessageId))
                .Select(x =>
                {
                    var candidate = byRemoteId[x.RemoteMessageId];
                    return new WinoSimilarMailItem(candidate.UniqueId, candidate.Subject, candidate.Sender, ToUtc(candidate.ReceivedAt), x.Similarity);
                })
                .Take(10)
                .ToArray();
        }, cancellationToken);

    public async Task<Guid> CreateSuggestedReplyDraftAsync(
        WinoIntelligenceContext context,
        string replyText,
        CancellationToken cancellationToken = default)
    {
        var account = await _accountService.GetAccountAsync(context.LocalAccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mail account no longer exists.");
        var mime = await _mimeFileService.GetMimeMessageInformationAsync(context.FileId, context.LocalAccountId, cancellationToken).ConfigureAwait(false);
        var referenceCopy = await _mailService.GetSingleMailItemAsync(context.MailUniqueId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The referenced message no longer exists.");
        var options = new DraftCreationOptions
        {
            Reason = DraftCreationReason.Reply,
            InitialBodyText = replyText,
            ReferencedMessage = new ReferencedMessage { MailCopy = referenceCopy, MimeMessage = mime.MimeMessage },
        };
        var (draftCopy, draftMime) = await _mailService.CreateDraftAsync(context.LocalAccountId, options).ConfigureAwait(false);
        var preparation = new DraftPreparationRequest(account, draftCopy, draftMime, options.Reason, referenceCopy);
        await _requestDelegator.ExecuteAsync(preparation).ConfigureAwait(false);
        return draftCopy.UniqueId;
    }

    public void CancelRequest(Guid requestId)
    {
        if (_requests.TryGetValue(requestId, out var pending))
            pending.Cancellation.Cancel();
    }

    public void CancelContext(string contentKey)
    {
        foreach (var request in _requests.Where(x => string.Equals(x.Value.ContentKey, contentKey, StringComparison.Ordinal)).ToArray())
            request.Value.Cancellation.Cancel();
    }

    public void InvalidateAccess()
    {
        _ = _localStore.DeleteAccessSnapshotsAsync();
    }

    private void InvalidateAccessAndNotify()
    {
        InvalidateAccess();
        WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        foreach (var request in _requests.Values)
            request.Cancellation.Cancel();
    }

    private async Task<AccessSnapshot> GetAccessAsync(WinoIntelligenceContext context, CancellationToken cancellationToken)
    {
        // Reader initialization must stay local. Authentication refreshes and entitlement API calls
        // are explicit account/intelligence-management operations, never a side effect of opening mail.
        var winoAccount = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (winoAccount is null)
            return AccessSnapshot.None;

        if (_accountSnapshotService is not null)
        {
            var accountSnapshot = await _accountSnapshotService.GetCachedAsync(winoAccount.Id, cancellationToken).ConfigureAwait(false);
            if (accountSnapshot is not null)
            {
                if (accountSnapshot.Billing?.AiPack.HasAccess != true)
                    return AccessSnapshot.None;

                var mailbox = accountSnapshot.Mailboxes.FirstOrDefault(x =>
                    x.ProviderType == (int)context.ProviderType &&
                    string.Equals(x.Address.Trim(), context.AccountAddress.Trim(), StringComparison.OrdinalIgnoreCase));
                var hasConsent = accountSnapshot.Consent is { } consent && IsCurrent(consent);
                return new(true, hasConsent, mailbox?.MailboxId);
            }
        }

        // Keep compatibility with the earlier account-scoped cache while existing installations
        // migrate to WinoAccountIntelligenceSnapshot. A cache miss intentionally means no access.
        var persisted = await _localStore.GetAccessSnapshotAsync(context.LocalAccountId, cancellationToken).ConfigureAwait(false);
        if (persisted is not null && persisted.WinoAccountId == winoAccount.Id)
            return new(persisted.HasAiPack, persisted.HasIntelligenceConsent, persisted.MailboxId);

        return AccessSnapshot.None;
    }

    private async Task<WinoIntelligenceOperationResult<T>> RunAsync<T>(
        WinoIntelligenceContext context,
        Guid requestId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        CancelRequest(requestId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new PendingRequest(context.ContentKey, linked);
        _requests[requestId] = pending;
        try
        {
            var value = await action(linked.Token).ConfigureAwait(false);
            return new(requestId, context.ContentKey, value, false, null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(requestId, context.ContentKey, default, true, null);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ExecuteWinoIntelligenceAction");
            return new(requestId, context.ContentKey, default, false, WinoAccountApiErrorTranslator.Translate(exception.Message));
        }
        finally
        {
            if (_requests.TryGetValue(requestId, out var current) && ReferenceEquals(current, pending))
                _requests.TryRemove(requestId, out _);
            linked.Dispose();
        }
    }

    private async Task<IReadOnlyList<WinoSuggestedReplyMessage>> PrepareMessagesAsync(
        Guid accountId,
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        MailContentProcessor processor,
        CancellationToken cancellationToken)
    {
        var output = new List<WinoSuggestedReplyMessage>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var content = await _messageResolver.GetContentAsync(accountId, candidate, cancellationToken).ConfigureAwait(false);
            var from = content.From.Count > 0 ? content.From : [new MailAddress(candidate.Sender, candidate.SenderName)];
            var prepared = processor.Prepare(from, candidate.Subject, content.Body, EmbeddingProfile.OpenAiTextEmbedding3Small768);
            output.Add(ToReplyMessage(candidate, prepared));
        }
        return output;
    }

    private static WinoSuggestedReplyMessage ToReplyMessage(IntelligenceMessageCandidate candidate, PreparedMailContent prepared)
        => new(candidate.RemoteMessageId, prepared.ContentHash, prepared.Subject, prepared.Sender, prepared.Body,
            ToUtc(candidate.ReceivedAt), candidate.IsOutgoing);

    private static MailContentProcessor CreateProcessor()
        => new(new HtmlContentSanitizer());

    private static string RequireSummary(ApiEnvelope<AiSummaryResultDto> response, string fallback)
        => response.IsSuccess && response.Result is not null && !string.IsNullOrWhiteSpace(response.Result.Text)
            ? response.Result.Text
            : throw new InvalidOperationException(response.ErrorCode ?? fallback);

    private static MailTranslationResult RequireTranslation(ApiEnvelope<AiTranslationResultDto> response, string fallback)
        => response.IsSuccess && response.Result is not null &&
           !string.IsNullOrWhiteSpace(response.Result.DetectedSourceLanguage) &&
           response.Result.Translations.Count > 0
            ? new MailTranslationResult(response.Result.DetectedSourceLanguage, response.Result.Translations)
            : throw new InvalidOperationException(response.ErrorCode ?? fallback);

    private static string CreateSummaryCacheKey(MailContentProjection projection, string language)
        => $"{projection.Version}-{projection.ContentHash}-{language}";

    private static IReadOnlyList<MailContentSegment> CreateSummarySegments(IReadOnlyList<MailContentSegment> segments)
    {
        const int maximumSegmentLength = 10_000;
        const int maximumTotalLength = 120_000;
        var output = new List<MailContentSegment>();
        var remaining = maximumTotalLength;
        foreach (var segment in segments)
        {
            var text = ProtectedProjectionMarker().Replace(segment.Text, string.Empty).Trim();
            while (text.Length > 0 && remaining > 0 && output.Count < 500)
            {
                var length = Math.Min(Math.Min(text.Length, maximumSegmentLength), remaining);
                if (length < text.Length)
                {
                    var split = text.LastIndexOfAny(['\r', '\n', ' '], length - 1, length);
                    if (split >= length / 2)
                        length = split + 1;
                }
                var chunk = text[..length].Trim();
                text = text[length..].TrimStart();
                if (chunk.Length == 0)
                    continue;
                output.Add(new MailContentSegment(
                    $"s{output.Count + 1:000000}",
                    MailContentSection.CurrentMessage,
                    segment.Kind,
                    chunk));
                remaining -= chunk.Length;
            }
            if (remaining == 0 || output.Count == 500)
                break;
        }
        return output;
    }

    [GeneratedRegex(@"⟦/?[ip]\d+⟧", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedProjectionMarker();

    private static string CreateTranslationCacheKey(MailContentProjection projection, string? sourceLanguage, string targetLanguage)
        => $"{projection.Version}-{projection.ContentHash}-{sourceLanguage ?? "detect"}-{targetLanguage}-gpt5mini-v1";

    private static (bool NeedsReply, string NeedsReplyDetail, WinoIntelligenceDeadline? Deadline) ParseArtifacts(
        IReadOnlyList<IntelligenceArtifactDto> artifacts)
    {
        var fact = artifacts.FirstOrDefault(x => !x.IsDeleted && x.Capability == IntelligenceCapability.BriefingFact)?.BriefingFact;
        var metadata = new MailIntelligenceMetadata(string.Empty, [], fact, string.Empty);
        var needsReply = metadata.NeedsReply?.Value == true;
        var detail = string.Empty;
        var deadline = metadata.Deadline;
        if (deadline?.HasDeadline != true)
            return (needsReply, detail, null);
        return (needsReply, detail, new WinoIntelligenceDeadline(
            deadline.Action,
            deadline.DueAtUtc,
            deadline.LocalDate,
            deadline.LocalDateEnd,
            deadline.TimeZoneId,
            deadline.Precision,
            deadline.Confidence));
    }

    private static bool IsCurrent(IntelligenceConsentDto consent)
        => consent.Status == ConsentStatuses.Active && consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private sealed record PendingRequest(string ContentKey, CancellationTokenSource Cancellation);
    private sealed record AccessSnapshot(bool HasAiPack, bool HasIntelligenceConsent, Guid? MailboxId)
    {
        public static AccessSnapshot None { get; } = new(false, false, null);
    }
}
