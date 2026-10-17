#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.PushRelay;

/// <summary>What this install last registered with a provider for one account. Secrets stay here only as needed for re-registration.</summary>
public sealed class AppManagedRegistrationRecord
{
    /// <summary><c>gmail</c> or <c>graph</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gmail: the mailbox address the watch was created for. Graph: the subscription id.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Graph only: the random client state the subscription was created with.</summary>
    public string? ClientState { get; set; }

    /// <summary>Gmail only: the history id the watch started at.</summary>
    public string? HistoryId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public bool RenewalDue { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppManagedRegistrationRecord))]
internal sealed partial class AppManagedRegistrationJsonContext : JsonSerializerContext;

/// <summary>Persists <see cref="AppManagedRegistrationRecord"/> per account in non-roaming configuration.</summary>
public sealed class PushRelayAppManagedRegistrationStore(IConfigurationService configuration) : IPushRelayAppManagedRegistrationStore
{
    private const string KeyPrefix = "PushRelay.AppManaged.";
    private readonly ILogger _logger = Log.ForContext<PushRelayAppManagedRegistrationStore>();
    private readonly object _gate = new();

    public AppManagedRegistrationRecord? Load(Guid accountId)
    {
        lock (_gate)
        {
            var json = configuration.Get<string>(Key(accountId), string.Empty);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize(json, AppManagedRegistrationJsonContext.Default.AppManagedRegistrationRecord); }
            catch (JsonException ex)
            {
                _logger.Warning("Stored provider registration for account {AccountId} could not be read ({Error}).", accountId, ex.GetType().Name);
                configuration.Remove(Key(accountId));
                return null;
            }
        }
    }

    public void Save(Guid accountId, AppManagedRegistrationRecord record)
    {
        lock (_gate)
            configuration.Set(Key(accountId), JsonSerializer.Serialize(record, AppManagedRegistrationJsonContext.Default.AppManagedRegistrationRecord));
    }

    public void MarkRenewalDue(Guid accountId)
    {
        lock (_gate)
        {
            var record = Load(accountId);
            if (record is null) return;
            record.RenewalDue = true;
            Save(accountId, record);
        }
    }

    public void Forget(Guid accountId)
    {
        lock (_gate) configuration.Remove(Key(accountId));
    }

    private static string Key(Guid accountId) => KeyPrefix + accountId.ToString("D");
}
