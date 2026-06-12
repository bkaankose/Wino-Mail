using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc.Transport;

namespace Wino.BackgroundService;

/// <summary>
/// Lifetime state machine for the companion process:
/// Starting → Serving(n clients). On last client disconnect the process either stays
/// resident (tray preference) or lingers briefly and exits. Zero configured accounts
/// also exits unless a client is connected.
/// </summary>
public sealed partial class CompanionLifecycle : IDisposable
{
    private static readonly TimeSpan ExitLinger = TimeSpan.FromSeconds(10);

    private readonly NamedPipeRpcServerHost _serverHost;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly Action _terminateAction;
    private readonly ILogger _logger = Log.ForContext<CompanionLifecycle>();

    private CancellationTokenSource? _lingerCts;

    public CompanionLifecycle(NamedPipeRpcServerHost serverHost,
                              IPreferencesService preferencesService,
                              IAccountService accountService,
                              Action terminateAction)
    {
        _serverHost = serverHost;
        _preferencesService = preferencesService;
        _accountService = accountService;
        _terminateAction = terminateAction;

        _serverHost.ClientConnected += (_, handshake) =>
        {
            CancelPendingExit();
            _logger.Information("Client connected ({ClientName} {AppVersion}). Connections: {Count}", handshake.ClientName, handshake.AppVersion, _serverHost.ConnectionCount);
        };

        _serverHost.ClientDisconnected += (_, remaining) =>
        {
            _logger.Information("Client disconnected. Remaining connections: {Count}", remaining);

            if (remaining == 0)
            {
                OnLastClientDisconnected();
            }
        };

        _preferencesService.PreferenceChanged += OnPreferenceChanged;
    }

    /// <summary>
    /// Applies the startup policy: with zero accounts and no client, the companion has
    /// nothing to do and exits after the linger window (a client may still be starting up).
    /// </summary>
    public async Task EvaluateStartupAsync()
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            if (accounts.Count == 0 && _serverHost.ConnectionCount == 0)
            {
                _logger.Information("No accounts configured; scheduling exit unless a client connects.");
                ScheduleExit();
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Startup lifecycle evaluation failed.");
        }
    }

    private void OnPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IPreferencesService.AppCloseBehavior))
            return;

        // If the user switched away from tray residency while no client is connected,
        // re-evaluate whether the companion should exit.
        if (_serverHost.ConnectionCount == 0)
        {
            OnLastClientDisconnected();
        }
    }

    private void OnLastClientDisconnected()
    {
        if (ShouldStayResident())
        {
            _logger.Information("Last client disconnected; staying resident in the tray.");
            return;
        }

        _logger.Information("Last client disconnected; exiting after linger window.");
        ScheduleExit();
    }

    private bool ShouldStayResident()
        => _preferencesService.AppCloseBehavior is AppCloseBehavior.RunInBackgroundWithTrayIcon or AppCloseBehavior.RunInBackgroundWithoutTrayIcon;

    private void ScheduleExit()
    {
        CancelPendingExit();

        var cts = new CancellationTokenSource();
        _lingerCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ExitLinger, cts.Token).ConfigureAwait(false);

                if (_serverHost.ConnectionCount == 0 && !ShouldStayResident())
                {
                    _logger.Information("Linger expired with no clients; terminating companion.");
                    _terminateAction();
                }
            }
            catch (OperationCanceledException)
            {
                // A client connected in time.
            }
        });
    }

    private void CancelPendingExit()
    {
        _lingerCts?.Cancel();
        _lingerCts?.Dispose();
        _lingerCts = null;
    }

    public void Dispose()
    {
        _preferencesService.PreferenceChanged -= OnPreferenceChanged;
        CancelPendingExit();
    }
}
