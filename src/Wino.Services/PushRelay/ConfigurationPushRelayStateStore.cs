#nullable enable
using System;
using System.Text.Json;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// Keeps <see cref="PushRelayState"/> in non-roaming configuration. Preferences are avoided on purpose: they are
/// exported into profile sync, and this state belongs to one device.
/// </summary>
public sealed class ConfigurationPushRelayStateStore(IConfigurationService configuration) : IPushRelayStateStore
{
    private readonly ILogger _logger = Log.ForContext<ConfigurationPushRelayStateStore>();
    private readonly object _gate = new();

    public PushRelayState Load()
    {
        lock (_gate)
        {
            PushRelayState state;
            var json = configuration.Get<string>(PushRelayDefaults.StateConfigurationKey, string.Empty);
            try
            {
                state = string.IsNullOrWhiteSpace(json)
                    ? new PushRelayState()
                    : JsonSerializer.Deserialize(json, PushRelayJsonContext.Default.PushRelayState) ?? new PushRelayState();
            }
            catch (JsonException ex)
            {
                _logger.Warning("Stored relay state could not be read ({Error}); starting over.", ex.GetType().Name);
                state = new PushRelayState();
            }

            state.Mailboxes ??= [];
            state.UserOptIn = configuration.Get(PushRelayDefaults.OptInConfigurationKey, false);
            return state;
        }
    }

    public void Save(PushRelayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            configuration.Set(PushRelayDefaults.OptInConfigurationKey, state.UserOptIn);
            configuration.Set(PushRelayDefaults.StateConfigurationKey, JsonSerializer.Serialize(state, PushRelayJsonContext.Default.PushRelayState));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            configuration.Remove(PushRelayDefaults.StateConfigurationKey);
        }
    }
}
