using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.WinUI.Services.Companion;

namespace Wino.Mail.WinUI.Services;

internal sealed class MainTrayController
{
    private readonly NativeTrayIcon _icon;
    private readonly ICompanionService _companion;
    private readonly Func<Task> _openMail;
    private readonly Func<Task> _openCalendar;
    private readonly Func<Task> _exit;
    private bool _isCompanionEnabled;
    private bool _isHotKeyEnabled;
    private HotKeyGesture _hotKey = HotKeyGesture.Default;

    public MainTrayController(DispatcherQueue dispatcher, IServiceProvider services,
        INativeAppService nativeAppService,
        CompanionNavigationCallbacks navigation,
        Func<Task> openMail, Func<Task> openCalendar, Func<Task> exit)
    {
        _openMail = openMail;
        _openCalendar = openCalendar;
        _exit = exit;
        _icon = new NativeTrayIcon(
            dispatcher,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Wino_Icon.ico"),
            "Wino Mail",
            BuildMenu,
            ToggleFlyoutAsync,
            OpenMailAsync,
            PrepareForTrayInteraction);
        _companion = new CompanionService(
            services,
            dispatcher,
            nativeAppService,
            () => _icon.TryGetIconRect(out var rect) ? (true, rect) : (false, default),
            navigation);
        _companion.SessionDisabled += CompanionSessionDisabled;
        _icon.TaskbarRecreated += TrayIconTaskbarRecreated;
        _icon.Show();
    }

    public Task SetCompanionEnabledAsync(bool enabled)
    {
        _isCompanionEnabled = enabled;
        _icon.TrySetHotKey(enabled && _isHotKeyEnabled ? _hotKey : null);
        return _companion.SetEnabledAsync(enabled);
    }

    public bool TryConfigureHotKey(bool enabled, HotKeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (!normalized.IsValid)
            return false;

        if (_isCompanionEnabled && !_icon.TrySetHotKey(enabled ? normalized : null))
            return false;

        _isHotKeyEnabled = enabled;
        _hotKey = normalized;
        return true;
    }

    public Task SetReadinessAsync(CompanionReadinessState state) => _companion.SetReadinessAsync(state);

    public async Task ShutdownAsync()
    {
        _companion.SessionDisabled -= CompanionSessionDisabled;
        _icon.TaskbarRecreated -= TrayIconTaskbarRecreated;
        await _companion.ShutdownAsync();
        _icon.Dispose();
    }

    private Task ToggleFlyoutAsync()
    {
        return _companion.ToggleAsync();
    }

    private void PrepareForTrayInteraction() => _companion.PrepareForTrayInteraction();

    private Task HideFlyoutAsync()
    {
        _companion.Hide();
        return Task.CompletedTask;
    }

    private void TrayIconTaskbarRecreated(object? sender, EventArgs args) => _companion.RepositionIfOpen();

    private void CompanionSessionDisabled(object? sender, string message)
        => _icon.ShowNotification(Translator.Companion_DisabledTitle, message);

    private async Task OpenMailAsync()
    {
        _ = HideFlyoutAsync();
        await _openMail();
    }

    private IReadOnlyList<NativeTrayIcon.NativeTrayMenuItem> BuildMenu()
    {
        _ = HideFlyoutAsync();
        return new NativeTrayIcon.NativeTrayMenuItem[]
        {
            new(Translator.SystemTrayMenu_ShowWino, OpenMailAsync, IsDefault: true),
            new(Translator.SystemTrayMenu_ShowWinoCalendar, _openCalendar),
            NativeTrayIcon.NativeTrayMenuItem.Separator(),
            new(Translator.SystemTrayMenu_ExitWino, _exit)
        };
    }
}
