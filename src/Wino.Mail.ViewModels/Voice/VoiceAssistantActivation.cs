#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;
using Wino.Services.Assistant;

namespace Wino.Mail.ViewModels.Voice;

/// <summary>
/// Keeps Wino Assistant out of the app for everyone without Wino Intelligence. The platform's bubble (its window, global
/// shortcuts and <see cref="VoiceBubbleController"/>) and the voice brain exist only while the entitlement grants access,
/// and are torn down when it ends.
/// </summary>
public sealed class VoiceAssistantActivation : IDisposable
{
    private readonly IWinoAccountIntelligenceSnapshotService _entitlements;
    private readonly IAgentRuntimeHost _runtime;
    private readonly Action<Action> _post;
    private readonly Action _activate;
    private readonly Action _deactivate;
    private bool _isActive;
    private bool _disposed;

    /// <param name="post">Runs an action on the UI thread; <paramref name="activate"/> and <paramref name="deactivate"/> run through it.</param>
    public VoiceAssistantActivation(IServiceProvider services, Action<Action> post, Action activate, Action deactivate)
    {
        _entitlements = services.GetRequiredService<IWinoAccountIntelligenceSnapshotService>();
        _runtime = services.GetRequiredService<IAgentRuntimeHost>();
        _post = post;
        _activate = activate;
        _deactivate = deactivate;
    }

    public void Start()
    {
        WeakReferenceMessenger.Default.Register<VoiceAssistantActivation, WinoIntelligenceEntitlementChanged>(this, static (activation, _) => activation._post(activation.Apply));
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _entitlements.GetEntitlementAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Wino Intelligence entitlement for Wino Assistant.");
            return;
        }

        _post(Apply);
    }

    // Reads the current entitlement instead of a message's, so a late answer from an older read can't undo a newer one.
    private void Apply()
    {
        if (_disposed)
            return;

        var hasAccess = _entitlements.CurrentEntitlement.CanAccessSurfaces;
        if (hasAccess == _isActive)
            return;

        _isActive = hasAccess;
        if (hasAccess)
        {
            _activate();
            _ = StartBrainAsync();
        }
        else
        {
            _deactivate();
        }
    }

    private async Task StartBrainAsync()
    {
        try
        {
            // The brain is local: loading it is cheap; with brain sync on, the check pulls another device's changes.
            await _runtime.EnsureInitializedAsync().ConfigureAwait(false);
            await _runtime.CheckSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The assistant brain could not load.");
        }
    }

    /// <summary>UI thread. Tears the assistant down if it is running.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        if (!_isActive)
            return;

        _isActive = false;
        _deactivate();
    }
}
