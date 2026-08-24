using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;

namespace Wino.SmokeTest.ConsoleApp;

internal sealed class StressWorkload
{
    private static readonly string[] Queries =
    [
        "project status updates and delivery risks",
        "invoices, receipts, and upcoming payments",
        "travel reservations and schedule changes",
        "messages requiring a reply or follow-up",
        "meetings, deadlines, and action items",
        "software releases and production incidents",
        "recruiting conversations and job opportunities",
        "account security alerts and sign-in notifications",
    ];

    private readonly StressOptions _options;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly Guid _mailboxId;
    private readonly string[] _remoteMessageIds;
    private readonly byte[][] _ingestionFixtures;
    private int _aiRequests;

    private StressWorkload(StressOptions options, IWinoAccountApiClient apiClient, Guid mailboxId,
        string[] remoteMessageIds, byte[][] ingestionFixtures)
    {
        _options = options;
        _apiClient = apiClient;
        _mailboxId = mailboxId;
        _remoteMessageIds = remoteMessageIds;
        _ingestionFixtures = ingestionFixtures;
    }

    public int AiRequests => Volatile.Read(ref _aiRequests);

    public static async Task<StressWorkload> CreateAsync(
        StressOptions options,
        IServiceProvider services,
        string runId,
        CancellationToken cancellationToken)
    {
        var accountService = services.GetRequiredService<IAccountService>();
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.SingleOrDefault(x => string.Equals(x.Address.Trim(), options.Account,
            StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
            $"Dedicated stress mailbox was not found: {options.Account}");
        var apiClient = services.GetRequiredService<IWinoAccountApiClient>();
        var mailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        var mailbox = mailboxes.SingleOrDefault(x => string.Equals(x.Address.Trim(), options.Account,
            StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
            "The dedicated stress account has no semantic mailbox. Index it before running the test.");

        var currentUser = await apiClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!currentUser.IsSuccess || currentUser.Result is null)
            throw new InvalidOperationException("The Wino API authentication preflight failed.");
        _ = await apiClient.GetIntelligenceStatusAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);

        var resolver = services.GetRequiredService<IIntelligenceMessageContextResolver>();
        var candidates = await resolver.GetCandidatesAsync(account.Id, null, cancellationToken).ConfigureAwait(false);
        var remoteIds = candidates.Select(static x => x.RemoteMessageId).Distinct(StringComparer.Ordinal).Take(2_000).ToArray();
        if (remoteIds.Length == 0) throw new InvalidOperationException("The stress mailbox has no messages.");

        var fixtures = await CreateIngestionFixturesAsync(services, mailbox.MailboxId, runId, cancellationToken)
            .ConfigureAwait(false);
        return new StressWorkload(options, apiClient, mailbox.MailboxId, remoteIds, fixtures);
    }

    public async Task<StressOperationResult> ExecuteAsync(long sequence, string phase, CancellationToken cancellationToken)
    {
        var operation = SelectOperation(_options.Profile, sequence);
        if (_options.Profile == StressProfile.Ai)
        {
            var count = Interlocked.Increment(ref _aiRequests);
            if (count > _options.AiRequestLimit!.Value)
                throw new OperationCanceledException("The AI request limit was reached.", cancellationToken);
        }

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var context = new StressOperationContext(operation);
        try
        {
            using (StressOperationContext.Push(context))
                await InvokeAsync(operation, sequence, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return BuildResult(started, phase, operation, stopwatch.Elapsed.TotalMilliseconds, context.Attempts, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return BuildResult(started, phase, operation, stopwatch.Elapsed.TotalMilliseconds, context.Attempts, exception);
        }
    }

    internal static string SelectOperation(StressProfile profile, long sequence)
    {
        var bucket = (int)(sequence % 100);
        return profile switch
        {
            StressProfile.Realistic when bucket < 55 => "search",
            StressProfile.Realistic when bucket < 70 => bucket % 2 == 0 ? "status" : "manifest",
            StressProfile.Realistic when bucket < 80 => "timeline",
            StressProfile.Realistic when bucket < 90 => "delta",
            StressProfile.Realistic when bucket < 95 => "artifacts",
            StressProfile.Realistic => "ingest",
            StressProfile.Database when bucket < 60 => "search",
            StressProfile.Database when bucket < 80 => "delta",
            StressProfile.Database when bucket < 90 => bucket % 2 == 0 ? "timeline" : "artifacts",
            StressProfile.Database => "ingest",
            StressProfile.Ai => "planner-search",
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    private async Task InvokeAsync(string operation, long sequence, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "search":
            case "planner-search":
                await _apiClient.SearchIntelligenceAsync(new IntelligenceSemanticSearchRequest(
                    Queries[(int)(sequence % Queries.Length)], [new IntelligenceMailboxSearchScopeDto(_mailboxId)], 10,
                    null, TimeZoneInfo.Local.Id, CultureInfo.CurrentUICulture.Name, operation == "planner-search"),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "status": await _apiClient.GetIntelligenceStatusAsync(_mailboxId, cancellationToken).ConfigureAwait(false); break;
            case "manifest": await _apiClient.GetIntelligenceManifestAsync(cancellationToken).ConfigureAwait(false); break;
            case "timeline":
                await _apiClient.GetIntelligenceCoverageTimelineAsync(_mailboxId, DateTimeOffset.UtcNow.AddYears(-1),
                    DateTimeOffset.UtcNow, 72, cancellationToken).ConfigureAwait(false);
                break;
            case "delta":
                var sizes = new[] { 10, 100, 1_000 };
                var size = Math.Min(sizes[(int)(sequence % sizes.Length)], _remoteMessageIds.Length);
                var offset = (int)(sequence % _remoteMessageIds.Length);
                var ids = Enumerable.Range(0, size).Select(i => _remoteMessageIds[(offset + i) % _remoteMessageIds.Length]).ToArray();
                await _apiClient.ResolveIntelligenceDeltaAsync(_mailboxId, ids, cancellationToken).ConfigureAwait(false);
                break;
            case "artifacts": await _apiClient.GetIntelligenceArtifactsAsync(_mailboxId, null, 100, cancellationToken).ConfigureAwait(false); break;
            case "ingest": await _apiClient.IngestIntelligenceAsync(_mailboxId,
                _ingestionFixtures[(int)(sequence % _ingestionFixtures.Length)], cancellationToken).ConfigureAwait(false); break;
        }
    }

    private static StressOperationResult BuildResult(DateTimeOffset started, string phase, string route, double duration,
        IReadOnlyList<StressAttempt> attempts, Exception? exception)
    {
        var last = attempts.LastOrDefault();
        var exceptionFailure = exception switch
        {
            OperationCanceledException => StressFailureKind.Timeout,
            HttpRequestException => StressFailureKind.Transport,
            _ when exception is not null => StressFailureKind.Validation,
            _ => StressFailureKind.None,
        };
        var failure = last is null || last.FailureKind == StressFailureKind.None
            ? exceptionFailure
            : last.FailureKind;
        return new StressOperationResult(started, phase, route, duration, exception is null && failure == StressFailureKind.None,
            failure, last?.StatusCode, attempts.Sum(static x => x.RequestBytes), attempts.Sum(static x => x.ResponseBytes),
            last?.RetryAfter, attempts.Where(static x => x.StatusCode.HasValue).Select(static x => x.StatusCode!.Value).ToArray(),
            attempts.Count);
    }

    private static async Task<byte[][]> CreateIngestionFixturesAsync(IServiceProvider services, Guid mailboxId,
        string runId, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<IDatabaseService>();
        var winoAccount = await database.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for stress ingestion fixtures.");
        var encryptor = services.GetRequiredService<IContentEnvelopeEncryptor>();
        var fixtures = new byte[20][];
        var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/ingest";
        for (var index = 0; index < fixtures.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new IngestIntelligenceRequest
            {
                Language = "en-US",
                Documents =
                [
                    new IntelligenceIngestDocumentRequest
                    {
                        RemoteMessageId = $"stress:{runId}:{index:D4}",
                        ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{runId}:{index}"))).ToLowerInvariant(),
                        CanonicalContent = $"Synthetic Wino intelligence capacity fixture {runId} item {index}. Project status, deadline, invoice, and follow-up.",
                        OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-index),
                        SenderAddresses = ["stress-fixture@winomail.app"],
                        SenderDomains = ["winomail.app"],
                        ProviderFolderIds = ["stress-fixtures"],
                    },
                ],
            };
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(request);
            var encrypted = encryptor.Encrypt(plaintext,
                new ContentEnvelopeContext(winoAccount.Id, mailboxId, route), Guid.NewGuid(), DateTimeOffset.UtcNow);
            try { fixtures[index] = ContentEnvelopeBinaryCodec.Encode(encrypted); }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encrypted.WrappedKey);
                CryptographicOperations.ZeroMemory(encrypted.Nonce);
                CryptographicOperations.ZeroMemory(encrypted.Tag);
                CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
            }
        }
        return fixtures;
    }
}
