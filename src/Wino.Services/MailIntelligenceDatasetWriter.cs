#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Services;

/// <summary>
/// Debug builds only: writes every prepared upload envelope to disk as plain text, so the
/// messages the server sees can be collected into an offline data set for evaluating insights.
/// Files go to &lt;app data&gt;\IntelligenceDataset\&lt;account id&gt;\ and are named by the message
/// date and a hash of its remote id, so a message prepared again overwrites its own file.
/// The files hold mail content in plaintext; release builds never write them.
/// </summary>
internal sealed class MailIntelligenceDatasetWriter(IApplicationConfiguration? applicationConfiguration)
{
    public const string DatasetFolderName = "IntelligenceDataset";

    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<MailIntelligenceDatasetWriter>();

    public static bool IsEnabled
#if DEBUG
        => true;
#else
        => false;
#endif

    public void Write(
        Guid accountId,
        string accountAddress,
        IntelligenceMessageCandidate candidate,
        SemanticMailContent content,
        MailIntelligenceUploadEnvelopeDto envelope)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(applicationConfiguration?.ApplicationDataFolderPath))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, DatasetFolderName, accountId.ToString("N"));
            Directory.CreateDirectory(folder);

            var idHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.RemoteMessageId)))[..16];
            var fileName = $"{envelope.OccurredAtUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{idHash}.txt";
            File.WriteAllText(Path.Combine(folder, fileName), Format(accountAddress, candidate, content, envelope), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            // A debug aid must never fail a job.
            Logger.Warning(exception, "Could not write the intelligence data set entry for {RemoteMessageId}", envelope.RemoteMessageId);
        }
    }

    internal static string Format(
        string accountAddress,
        IntelligenceMessageCandidate candidate,
        SemanticMailContent content,
        MailIntelligenceUploadEnvelopeDto envelope)
    {
        var builder = new StringBuilder();
        void Line(string name, object? value) => builder.Append(name).Append(": ").AppendLine(value?.ToString() ?? string.Empty);

        Line("Account", accountAddress);
        Line("RemoteMessageId", envelope.RemoteMessageId);
        Line("ThreadId", candidate.ThreadId);
        Line("ContentHash", envelope.ContentHash);
        Line("Date", envelope.OccurredAtUtc.ToString("O"));
        Line("From", envelope.Sender);
        Line("To", string.Join(", ", content.ToRecipients));
        Line("Cc", string.Join(", ", content.CcRecipients));
        Line("Subject", envelope.Subject);
        Line("IsOutgoing", envelope.IsOutgoing);
        Line("IsDirectRecipient", envelope.IsDirectRecipient);
        Line("HasLaterOutgoingReply", envelope.HasLaterOutgoingReply);
        Line("IsRead", candidate.IsRead);
        Line("IsFlagged", candidate.IsFlagged);
        Line("ProviderImportance", envelope.ProviderImportance);
        Line("HasListUnsubscribe", envelope.HasListUnsubscribe);
        Line("RemoteFolderIds", string.Join(", ", envelope.RemoteFolderIds ?? []));
        Line("Attachments", string.Join(", ", content.Attachments.Select(static a => $"{a.FileName} ({a.MediaType})")));
        Line("SourceBodyFormat", content.Body.Format);
        builder.AppendLine("Body:");
        builder.AppendLine(envelope.Body);
        return builder.ToString();
    }
}
