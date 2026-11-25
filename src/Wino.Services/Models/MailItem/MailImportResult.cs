namespace Wino.Services.Models.MailItem;

/// <summary>The server's id of the stored copy, when the server returns one.</summary>
public sealed record MailImportResult(string RemoteId);
