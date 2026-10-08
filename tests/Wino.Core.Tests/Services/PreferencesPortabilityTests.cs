using System.Text.Json;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class PreferencesPortabilityTests
{
    [Fact]
    public void ExportAndImport_PreserveValuesAndKeepInstallationIdentity()
    {
        var source = new PreferencesService(new MemoryConfiguration());
        source.RenderImages = false;
        source.MarkAsDelay = 12;
        source.AppCloseBehavior = AppCloseBehavior.RunInBackgroundWithoutTrayIcon;
        var exported = source.ExportPreferences();

        using var json = JsonDocument.Parse(exported);
        json.RootElement.TryGetProperty(nameof(IPreferencesService.DiagnosticId), out _).Should().BeFalse();

        var destination = new PreferencesService(new MemoryConfiguration());
        var diagnosticId = destination.DiagnosticId;
        var imported = destination.ImportPreferences(exported);

        imported.failedCount.Should().Be(0);
        destination.RenderImages.Should().BeFalse();
        destination.MarkAsDelay.Should().Be(12);
        destination.AppCloseBehavior.Should().Be(AppCloseBehavior.RunInBackgroundWithoutTrayIcon);
        destination.DiagnosticId.Should().Be(diagnosticId);
    }

    [Fact]
    public void Import_UsesCurrentCloseBehaviorAheadOfLegacyTrayKeyAndNotifies()
    {
        var preferences = new PreferencesService(new MemoryConfiguration());
        var changes = new List<string>();
        preferences.PreferenceChanged += (_, property) => changes.Add(property);

        preferences.ImportPreferences("""
            {"AppCloseBehavior": "RunInBackgroundWithoutTrayIcon", "IsSystemTrayIconEnabled": true, "MarkAsDelay": -3}
            """);

        preferences.AppCloseBehavior.Should().Be(AppCloseBehavior.RunInBackgroundWithoutTrayIcon);
        preferences.IsSystemTrayIconEnabled.Should().BeFalse();
        preferences.MarkAsDelay.Should().Be(0);
        changes.Should().Contain(nameof(IPreferencesService.AppCloseBehavior));
        changes.Should().Contain(nameof(IPreferencesService.IsSystemTrayIconEnabled));
        changes.Should().Contain(nameof(IPreferencesService.MarkAsDelay));
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public bool Contains(string key) => _values.ContainsKey(key);
        public bool Remove(string key) => _values.Remove(key);
        public void Set(string key, object value) => _values[key] = value;
        public T Get<T>(string key, T defaultValue = default!)
            => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void SetRoaming(string key, object value) => Set(key, value);
        public T GetRoaming<T>(string key, T defaultValue = default!) => Get(key, defaultValue);
    }
}
