// Copyright (c) 0x5BFA. All rights reserved.
// Licensed under the MIT license.


using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

internal abstract partial class DesktopFlyoutSystemBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, TargetState> _targets = [];
    private SystemBackdropTheme? _requestedTheme;

    protected abstract ISystemBackdropControllerWithTargets? TryCreateController(SystemBackdropConfiguration configuration);

    /// <summary>
    /// Follows the flyout's element theme instead of the default configuration, which does
    /// not track RequestedTheme changes on the island content.
    /// </summary>
    internal void SetTheme(ElementTheme theme)
    {
        _requestedTheme = theme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => null
        };

        foreach (var (target, state) in _targets.ToArray())
            UpdateTarget(target, state);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        if (_targets.ContainsKey(target))
            return;

        var configuration = CreateConfiguration(target, xamlRoot);
        var controller = TryCreateController(configuration);
        if (controller is null)
            return;

        controller.SetSystemBackdropConfiguration(configuration);
        controller.AddSystemBackdropTarget(target);

        _targets[target] = new(controller, configuration, xamlRoot);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);

        if (!_targets.Remove(target, out var state))
            return;

        ReleaseController(target, state.Controller);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);

        if (!_targets.TryGetValue(target, out var state))
            return;

        UpdateTarget(target, state with { XamlRoot = xamlRoot });
    }

    private void UpdateTarget(ICompositionSupportsSystemBackdrop target, TargetState state)
    {
        var previousTheme = state.Configuration.Theme;
        ApplyConfiguration(state.Configuration, target, state.XamlRoot);

        // Controllers carry explicit tint and luminosity values picked for one theme.
        // Changing only the configuration theme leaves those colors in place, so rebuild.
        if (state.Configuration.Theme != previousTheme)
        {
            ReleaseController(target, state.Controller);
            _targets.Remove(target);

            var controller = TryCreateController(state.Configuration);
            if (controller is null)
                return;

            controller.SetSystemBackdropConfiguration(state.Configuration);
            controller.AddSystemBackdropTarget(target);
            _targets[target] = state with { Controller = controller };
            return;
        }

        state.Controller.SetSystemBackdropConfiguration(state.Configuration);
        _targets[target] = state;
    }

    private static void ReleaseController(ICompositionSupportsSystemBackdrop target, ISystemBackdropControllerWithTargets controller)
    {
        controller.RemoveSystemBackdropTarget(target);

        if (controller is IDisposable disposable)
            disposable.Dispose();
    }

    private SystemBackdropConfiguration CreateConfiguration(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var configuration = new SystemBackdropConfiguration();
        ApplyConfiguration(configuration, target, xamlRoot);

        return configuration;
    }

    private void ApplyConfiguration(SystemBackdropConfiguration configuration, ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var defaultConfiguration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);

        configuration.Theme = _requestedTheme ?? defaultConfiguration.Theme;
        configuration.IsInputActive = true;
    }

    private sealed record TargetState(ISystemBackdropControllerWithTargets Controller, SystemBackdropConfiguration Configuration, XamlRoot XamlRoot);
}

internal sealed partial class CompanionMicaBackdrop : DesktopFlyoutSystemBackdrop
{
    protected override ISystemBackdropControllerWithTargets? TryCreateController(SystemBackdropConfiguration configuration)
    {
        var controller = BackdropControllerHelpers.GetMicaController(configuration.Theme);
        if (controller is not null)
        {
            controller.Kind = MicaKind.Base;
            return controller;
        }

        return BackdropControllerHelpers.GetAcrylicController(configuration.Theme);
    }
}
