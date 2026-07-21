using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Compatibility registration for the WinUI 3 build while preferences live in the
/// UI-agnostic services assembly shared by the UWP client and companion.
/// </summary>
public sealed class PreferencesService(IConfigurationService configurationService)
    : Wino.Services.PreferencesService(configurationService);
