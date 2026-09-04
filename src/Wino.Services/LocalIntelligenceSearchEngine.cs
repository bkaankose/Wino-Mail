#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class LocalIntelligenceSearchEngine(ILocalIntelligenceStore store) : ILocalIntelligenceSearchEngine
{
    private const int Dimensions = 768;
    private const double MinimumSimilarity = 0.55;

    public async Task<IReadOnlyList<LocalIntelligenceSearchMatch>> SearchAsync(
        IntelligenceSearchPlanResultDto response,
        IReadOnlyList<LocalIntelligenceSearchScope> scopes,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var scopeByMailbox = scopes.ToDictionary(static scope => scope.MailboxId);
        var documents = new List<LocalIntelligenceSearchDocument>();
        foreach (var accountId in scopes.Select(static scope => scope.LocalAccountId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.AddRange(await store.GetSearchDocumentsAsync(accountId, cancellationToken).ConfigureAwait(false));
        }

        var matches = new List<RankedDocument>();
        foreach (var versionPlan in response.Plans)
        {
            var queryVector = DecodeVector(versionPlan.QueryEmbedding, versionPlan.QueryEmbeddingDimensions, versionPlan.QueryEmbeddingEncoding);
            var allowedMailboxes = versionPlan.MailboxIds.ToHashSet();
            foreach (var local in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowedMailboxes.Contains(local.MailboxId) || !scopeByMailbox.TryGetValue(local.MailboxId, out var scope))
                    continue;
                if (scope.AllowedFolderIds.Count > 0 && !local.Document.FolderIds.Any(scope.AllowedFolderIds.Contains))
                    continue;

                var branchScores = versionPlan.Plan.Branches.Count == 0
                    ? new[] { 0d }
                    : versionPlan.Plan.Branches
                        .Where(branch => MatchesBranch(local.Document, branch))
                        .Select(branch => PreferredRatio(local.Document, branch))
                        .ToArray();
                if (branchScores.Length == 0)
                    continue;

                var similarity = queryVector is null ? 0 : Cosine(local.EmbeddingBytes, queryVector);
                if (versionPlan.Plan.RetrievalMode != SearchRetrievalModeV1.Structured && similarity < MinimumSimilarity)
                    continue;
                var preferred = branchScores.Max();
                var relevance = versionPlan.Plan.RetrievalMode switch
                {
                    SearchRetrievalModeV1.Structured => preferred,
                    SearchRetrievalModeV1.Semantic => similarity,
                    _ => 0.85 * similarity + 0.15 * preferred,
                };
                matches.Add(new(local, similarity, relevance, versionPlan.Plan.Sort, versionPlan.Plan.GroupBy));
            }
        }

        return Order(matches)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(static match => new LocalIntelligenceSearchMatch(
                match.Local.LocalAccountId,
                match.Local.Document.ServerMessageKey,
                match.Local.Document,
                match.Similarity,
                match.Relevance))
            .ToArray();
    }

    public async Task<IReadOnlyList<LocalIntelligenceSearchMatch>> FindSimilarAsync(
        Guid localAccountId,
        string sourceRemoteMessageId,
        int limit,
        bool outgoingOnly,
        IReadOnlySet<string>? excludedRemoteMessageIds = null,
        CancellationToken cancellationToken = default)
    {
        var documents = await store.GetSearchDocumentsAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        var source = documents.FirstOrDefault(document =>
            string.Equals(document.Document.ServerMessageKey, sourceRemoteMessageId, StringComparison.Ordinal));
        if (source is null || DecodeVector(source.EmbeddingBytes) is not { } sourceVector)
            return [];

        var excluded = excludedRemoteMessageIds ?? new HashSet<string>(StringComparer.Ordinal);
        return documents
            .Where(document => !string.Equals(document.Document.ServerMessageKey, sourceRemoteMessageId, StringComparison.Ordinal) &&
                               !excluded.Contains(document.Document.ServerMessageKey) &&
                               (!outgoingOnly || document.Document.IsOutgoing))
            .Select(document => new { Document = document, Similarity = Cosine(document.EmbeddingBytes, sourceVector) })
            .Where(static item => item.Similarity >= MinimumSimilarity)
            .OrderByDescending(static item => item.Similarity)
            .ThenByDescending(static item => item.Document.Document.ReceivedAtUtc)
            .ThenBy(static item => item.Document.Document.ServerMessageKey, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .Select(static item => new LocalIntelligenceSearchMatch(
                item.Document.LocalAccountId,
                item.Document.Document.ServerMessageKey,
                item.Document.Document,
                item.Similarity,
                item.Similarity))
            .ToArray();
    }

    private static IEnumerable<RankedDocument> Order(IEnumerable<RankedDocument> source)
    {
        var items = source.ToArray();
        if (items.Length == 0)
            return items;
        var groupBy = items.SelectMany(static item => item.GroupBy).Distinct().ToArray();
        if (groupBy.Length > 0)
        {
            return items.GroupBy(item => string.Join('\u001f', groupBy.Select(field => GroupValue(item.Local.Document, field))))
                .OrderByDescending(static group => group.Max(static item => item.Relevance))
                .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => ApplySort(group, group.First().Sort));
        }
        return ApplySort(items, items[0].Sort);
    }

    private static IOrderedEnumerable<RankedDocument> ApplySort(
        IEnumerable<RankedDocument> source,
        IReadOnlyList<SearchSortV1> sort)
    {
        IOrderedEnumerable<RankedDocument>? ordered = null;
        foreach (var item in sort)
        {
            Func<RankedDocument, IComparable> selector = item.Field switch
            {
                SearchSortFieldV1.ReceivedDate => value => value.Local.Document.ReceivedAtUtc,
                SearchSortFieldV1.Amount => value => value.Local.Document.Analysis.Documents.Select(static document => document.Amount ?? 0).DefaultIfEmpty().Max(),
                SearchSortFieldV1.Urgency => value => UrgencyRank(value.Local.Document.Analysis.Urgency),
                _ => value => value.Relevance,
            };
            ordered = ordered is null
                ? item.Direction == SearchSortDirectionV1.Ascending ? source.OrderBy(selector) : source.OrderByDescending(selector)
                : item.Direction == SearchSortDirectionV1.Ascending ? ordered.ThenBy(selector) : ordered.ThenByDescending(selector);
        }
        return (ordered ?? source.OrderByDescending(static item => item.Relevance))
            .ThenByDescending(static item => item.Local.Document.ReceivedAtUtc)
            .ThenBy(static item => item.Local.Document.ServerMessageKey, StringComparer.Ordinal);
    }

    private static bool MatchesBranch(MessageIntelligenceDownloadDto document, SearchBooleanBranchV1 branch)
    {
        if (branch.Excluded.Any(predicate => MatchesPredicate(document, predicate)))
            return false;
        foreach (var family in Enum.GetValues<PredicateFamily>())
        {
            var predicates = branch.Required.Where(predicate => Family(predicate.Field) == family).ToArray();
            if (predicates.Length == 0)
                continue;
            if (!MatchesFamily(document, family, predicates))
                return false;
        }
        return true;
    }

    private static double PreferredRatio(MessageIntelligenceDownloadDto document, SearchBooleanBranchV1 branch)
        => branch.Preferred.Count == 0 ? 0 : branch.Preferred.Count(predicate => MatchesPredicate(document, predicate)) / (double)branch.Preferred.Count;

    private static bool MatchesFamily(MessageIntelligenceDownloadDto document, PredicateFamily family, IReadOnlyList<SearchPredicateV1> predicates)
        => family switch
        {
            PredicateFamily.Entity => document.Analysis.Entities.Any(entity => predicates.All(predicate => MatchEntity(entity, predicate))),
            PredicateFamily.Document => document.Analysis.Documents.Any(item => predicates.All(predicate => MatchDocument(document, item, predicate))),
            PredicateFamily.Action => document.Analysis.Actions.Any(action => predicates.All(predicate => MatchAction(action, predicate))),
            PredicateFamily.Temporal => document.Analysis.TemporalReferences.Any(temporal => predicates.All(predicate => MatchTemporal(temporal, predicate))),
            PredicateFamily.Attachment => document.Attachments.Any(attachment => predicates.All(predicate => MatchAttachment(attachment, predicate))),
            _ => predicates.All(predicate => MatchesPredicate(document, predicate)),
        };

    private static bool MatchesPredicate(MessageIntelligenceDownloadDto document, SearchPredicateV1 predicate)
        => Family(predicate.Field) switch
        {
            PredicateFamily.Entity => document.Analysis.Entities.Any(entity => MatchEntity(entity, predicate)),
            PredicateFamily.Document => document.Analysis.Documents.Any(item => MatchDocument(document, item, predicate)),
            PredicateFamily.Action => document.Analysis.Actions.Any(action => MatchAction(action, predicate)),
            PredicateFamily.Temporal => document.Analysis.TemporalReferences.Any(temporal => MatchTemporal(temporal, predicate)),
            PredicateFamily.Attachment => document.Attachments.Any(attachment => MatchAttachment(attachment, predicate)),
            _ => MatchRoot(document, predicate),
        };

    private static bool MatchRoot(MessageIntelligenceDownloadDto document, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.ReceivedDate => MatchDate(document.ReceivedAtUtc, predicate),
            SearchFieldV1.Sender => MatchStrings([document.Sender, .. document.SenderAddresses], predicate),
            SearchFieldV1.Recipient => MatchStrings(document.RecipientAddresses, predicate),
            SearchFieldV1.Direction => MatchStrings([document.IsOutgoing ? "outgoing" : "incoming"], predicate),
            SearchFieldV1.Folder => MatchStrings(document.FolderIds, predicate),
            SearchFieldV1.IsRead => MatchBoolean(document.IsRead, predicate),
            SearchFieldV1.IsFlagged => MatchBoolean(document.IsFlagged, predicate),
            SearchFieldV1.HasAttachments => MatchBoolean(document.HasAttachments, predicate),
            SearchFieldV1.IsDirectRecipient => MatchBoolean(document.IsDirectRecipient, predicate),
            SearchFieldV1.HasLaterOutgoingReply => MatchBoolean(document.HasLaterOutgoingReply, predicate),
            SearchFieldV1.Importance => MatchStrings([document.Importance], predicate),
            SearchFieldV1.SmartLabel => MatchStrings(document.Analysis.SmartLabels.Select(label => EnumText(label.Label)), predicate),
            SearchFieldV1.Category => MatchStrings([EnumText(document.Analysis.Category)], predicate),
            SearchFieldV1.Intent => MatchStrings([EnumText(document.Analysis.Intent)], predicate),
            SearchFieldV1.Urgency => MatchStrings([EnumText(document.Analysis.Urgency)], predicate),
            SearchFieldV1.Topic => MatchStrings(document.Analysis.Topics, predicate),
            SearchFieldV1.SourceLanguage => MatchStrings([document.Analysis.SourceLanguage], predicate),
            SearchFieldV1.Anomaly => MatchStrings(document.Analysis.Anomalies.Select(EnumText), predicate),
            _ => false,
        };

    private static bool MatchEntity(IntelligenceEntityV1 entity, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.EntityType => MatchStrings([EnumText(entity.Type)], predicate),
            SearchFieldV1.EntityName => MatchStrings([entity.SourceText, entity.NormalizedText], predicate),
            SearchFieldV1.EntityRole => MatchStrings([EnumText(entity.Role)], predicate),
            _ => false,
        };

    private static bool MatchDocument(MessageIntelligenceDownloadDto root, IntelligenceDocumentV1 document, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.DocumentType => MatchStrings([EnumText(document.Type)], predicate),
            SearchFieldV1.DocumentStatus => MatchStrings([EnumText(document.Status)], predicate),
            SearchFieldV1.DocumentReference => MatchStrings([document.Reference], predicate),
            SearchFieldV1.DocumentIssuer => MatchStrings(root.Analysis.Entities
                .Where(entity => entity.Id == document.IssuingOrganizationEntityId)
                .SelectMany(static entity => new[] { entity.SourceText, entity.NormalizedText }), predicate),
            SearchFieldV1.Amount => document.Amount is { } amount && MatchNumber(amount, predicate),
            SearchFieldV1.Currency => MatchStrings([document.Currency], predicate),
            _ => false,
        };

    private static bool MatchAction(IntelligenceActionV1 action, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.ActionType => MatchStrings([EnumText(action.Type)], predicate),
            SearchFieldV1.ActionStatus => MatchStrings([EnumText(action.Status)], predicate),
            _ => false,
        };

    private static bool MatchTemporal(TemporalReferenceV1 temporal, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.TemporalType => MatchStrings([EnumText(temporal.Type)], predicate),
            SearchFieldV1.TemporalRange => temporal.Start is { } start && MatchDateRange(start, temporal.End ?? start, predicate),
            _ => false,
        };

    private static bool MatchAttachment(MessageAttachmentMetadataV1 attachment, SearchPredicateV1 predicate)
        => predicate.Field switch
        {
            SearchFieldV1.AttachmentName => MatchStrings([attachment.FileName], predicate),
            SearchFieldV1.AttachmentMediaType => MatchStrings([attachment.MediaType], predicate),
            _ => false,
        };

    private static bool MatchStrings(IEnumerable<string> values, SearchPredicateV1 predicate)
    {
        var candidates = values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var expected = predicate.StringValues ?? (predicate.StringValue is null ? [] : [predicate.StringValue]);
        var match = predicate.Operator switch
        {
            SearchOperatorV1.Contains => candidates.Any(candidate => expected.Any(value => candidate.Contains(value, StringComparison.OrdinalIgnoreCase))),
            SearchOperatorV1.In => candidates.Any(candidate => expected.Any(value => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))),
            _ => candidates.Any(candidate => expected.Any(value => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))),
        };
        return predicate.Operator == SearchOperatorV1.NotEquals ? !match : match;
    }

    private static bool MatchBoolean(bool value, SearchPredicateV1 predicate)
        => predicate.Operator switch
        {
            SearchOperatorV1.NotEquals => value != predicate.BooleanValue,
            // Boolean metadata is always present in the persisted V1 document. "Exists"
            // therefore tests field presence, not whether the stored value happens to be true.
            SearchOperatorV1.Exists => true,
            _ => value == predicate.BooleanValue,
        };

    private static bool MatchNumber(decimal value, SearchPredicateV1 predicate)
        => predicate.Operator switch
        {
            SearchOperatorV1.Equals => value == predicate.NumberValue,
            SearchOperatorV1.NotEquals => value != predicate.NumberValue,
            SearchOperatorV1.GreaterThan => value > predicate.NumberValue,
            SearchOperatorV1.GreaterThanOrEqual => value >= predicate.NumberValue,
            SearchOperatorV1.LessThan => value < predicate.NumberValue,
            SearchOperatorV1.LessThanOrEqual => value <= predicate.NumberValue,
            SearchOperatorV1.Between => value >= predicate.NumberFrom && value <= predicate.NumberTo,
            _ => false,
        };

    private static bool MatchDate(DateTimeOffset value, SearchPredicateV1 predicate)
        => predicate.Operator switch
        {
            SearchOperatorV1.Equals => value == predicate.DateValue,
            SearchOperatorV1.NotEquals => value != predicate.DateValue,
            SearchOperatorV1.GreaterThan => value > predicate.DateValue,
            SearchOperatorV1.GreaterThanOrEqual => value >= predicate.DateValue,
            SearchOperatorV1.LessThan => value < predicate.DateValue,
            SearchOperatorV1.LessThanOrEqual => value <= predicate.DateValue,
            SearchOperatorV1.Between => value >= predicate.DateFrom && value <= predicate.DateTo,
            _ => false,
        };

    private static bool MatchDateRange(DateTimeOffset start, DateTimeOffset end, SearchPredicateV1 predicate)
        => predicate.Operator == SearchOperatorV1.Between
            ? start <= predicate.DateTo && end >= predicate.DateFrom
            : MatchDate(start, predicate);

    private static PredicateFamily Family(SearchFieldV1 field)
        => field switch
        {
            SearchFieldV1.EntityType or SearchFieldV1.EntityName or SearchFieldV1.EntityRole => PredicateFamily.Entity,
            SearchFieldV1.DocumentType or SearchFieldV1.DocumentStatus or SearchFieldV1.DocumentReference or SearchFieldV1.DocumentIssuer or SearchFieldV1.Amount or SearchFieldV1.Currency => PredicateFamily.Document,
            SearchFieldV1.ActionType or SearchFieldV1.ActionStatus => PredicateFamily.Action,
            SearchFieldV1.TemporalType or SearchFieldV1.TemporalRange => PredicateFamily.Temporal,
            SearchFieldV1.AttachmentName or SearchFieldV1.AttachmentMediaType => PredicateFamily.Attachment,
            _ => PredicateFamily.Root,
        };

    private static string GroupValue(MessageIntelligenceDownloadDto document, SearchFieldV1 field)
        => field switch
        {
            SearchFieldV1.Sender => document.Sender,
            SearchFieldV1.Category => EnumText(document.Analysis.Category),
            SearchFieldV1.Intent => EnumText(document.Analysis.Intent),
            SearchFieldV1.Urgency => EnumText(document.Analysis.Urgency),
            SearchFieldV1.SourceLanguage => document.Analysis.SourceLanguage,
            SearchFieldV1.Importance => document.Importance,
            _ => string.Empty,
        };

    private static int UrgencyRank(MessageUrgencyV1 urgency) => urgency switch
    {
        MessageUrgencyV1.Critical => 4,
        MessageUrgencyV1.High => 3,
        MessageUrgencyV1.Normal => 2,
        MessageUrgencyV1.Low => 1,
        _ => 0,
    };

    private static string EnumText<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static float[]? DecodeVector(string? encoded, int? dimensions, string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoded) || dimensions != Dimensions || !string.Equals(encoding, "float32-le", StringComparison.Ordinal))
            return null;
        try { return DecodeVector(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return null; }
    }

    private static float[]? DecodeVector(byte[] bytes)
    {
        if (bytes.Length != Dimensions * sizeof(float))
            return null;
        var values = new float[Dimensions];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float))));
            if (!float.IsFinite(values[index]))
                return null;
        }
        return values;
    }

    private static double Cosine(byte[] encoded, float[] right)
    {
        var left = DecodeVector(encoded);
        if (left is null)
            return double.NegativeInfinity;
        double dot = 0, leftMagnitude = 0, rightMagnitude = 0;
        for (var index = 0; index < Dimensions; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }
        return leftMagnitude == 0 || rightMagnitude == 0 ? double.NegativeInfinity : dot / Math.Sqrt(leftMagnitude * rightMagnitude);
    }

    private sealed record RankedDocument(
        LocalIntelligenceSearchDocument Local,
        double Similarity,
        double Relevance,
        IReadOnlyList<SearchSortV1> Sort,
        IReadOnlyList<SearchFieldV1> GroupBy);

    private enum PredicateFamily { Root, Entity, Document, Action, Temporal, Attachment }
}
