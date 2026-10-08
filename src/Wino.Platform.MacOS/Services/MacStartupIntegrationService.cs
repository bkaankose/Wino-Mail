using Foundation;
using ServiceManagement;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Launch at login through the app's own login item (<see cref="SMAppService.MainApp"/>, macOS 13+).
/// macOS may hold a new registration for approval in System Settings › General › Login Items;
/// that state is reported as <see cref="StartupBehaviorResult.DisabledByUser"/> and the pane is opened.
/// Ad-hoc signed debug bundles outside /Applications usually report NotFound.
/// </summary>
public sealed class MacStartupIntegrationService : IStartupIntegrationService
{
    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(Map(SMAppService.MainApp.Status)); }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not read the login item status.");
            return Task.FromResult(StartupBehaviorResult.Fatal);
        }
    }

    public Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var service = SMAppService.MainApp;
            var status = service.Status;
            if (isEnabled)
            {
                if (status == SMAppServiceStatus.Enabled) return Task.FromResult(StartupBehaviorResult.Enabled);
                if (status != SMAppServiceStatus.RequiresApproval && !service.Register(out NSError? error))
                {
                    status = service.Status;
                    // Registering an already pending item fails; approval is still the user's step.
                    if (status != SMAppServiceStatus.RequiresApproval)
                    {
                        Serilog.Log.Warning("Could not register the login item: {Error} (status {Status}).", error?.LocalizedDescription, status);
                        return Task.FromResult(StartupBehaviorResult.Fatal);
                    }
                }
                status = service.Status;
                if (status == SMAppServiceStatus.RequiresApproval)
                {
                    SMAppService.OpenSystemSettingsLoginItems();
                    return Task.FromResult(StartupBehaviorResult.DisabledByUser);
                }
                return Task.FromResult(status == SMAppServiceStatus.Enabled ? StartupBehaviorResult.Enabled : StartupBehaviorResult.Fatal);
            }

            if (status is SMAppServiceStatus.NotRegistered or SMAppServiceStatus.NotFound) return Task.FromResult(StartupBehaviorResult.Disabled);
            if (!service.Unregister(out NSError? unregisterError))
            {
                Serilog.Log.Warning("Could not unregister the login item: {Error}.", unregisterError?.LocalizedDescription);
                return Task.FromResult(Map(service.Status));
            }
            return Task.FromResult(StartupBehaviorResult.Disabled);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not change the login item.");
            return Task.FromResult(StartupBehaviorResult.Fatal);
        }
    }

    /// <summary>Plain status text for diagnostics (the debug bridge's login-status command).</summary>
    public static string DescribeStatus()
    {
        try { return SMAppService.MainApp.Status.ToString(); }
        catch (Exception error) { return "error: " + error.Message; }
    }

    private static StartupBehaviorResult Map(SMAppServiceStatus status) => status switch
    {
        SMAppServiceStatus.Enabled => StartupBehaviorResult.Enabled,
        SMAppServiceStatus.RequiresApproval => StartupBehaviorResult.DisabledByUser,
        _ => StartupBehaviorResult.Disabled
    };
}
