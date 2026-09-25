using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WhatsNewServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinoWhatsNewTests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationService _configuration = new();
    private readonly Mock<IAppMetadataService> _appMetadataService = new();

    public WhatsNewServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _appMetadataService.Setup(x => x.AppVersion).Returns("2.1.3.0");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task GetReleasesAsync_ReturnsNewestFirst_AndSkipsInvalidFiles()
    {
        WriteRelease("2.1.2", starred: true);
        WriteRelease("2.1.10");
        WriteRelease("2.1.3");
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_directory, "noversion.json"), """{ "features": [] }""");

        var releases = await CreateService().GetReleasesAsync();

        releases.Select(x => x.Version).Should().Equal("2.1.10", "2.1.3", "2.1.2");
        releases.Last().IsStarred.Should().BeTrue();
        releases.First().Features.Should().ContainSingle()
            .Which.ImageUri.Should().Be("ms-appx:///Assets/WhatsNew/feature-2.1.10.png");
    }

    [Fact]
    public async Task ShouldShowShellEntryAsync_IsTrue_WhenNotesExistForPackageVersion()
    {
        WriteRelease("2.1.3");

        (await CreateService().ShouldShowShellEntryAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldShowShellEntryAsync_IsFalse_WhenNoNotesForPackageVersion()
    {
        WriteRelease("2.1.2");

        (await CreateService().ShouldShowShellEntryAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task MarkOpenedForCurrentVersion_HidesEntryUntilNextVersion()
    {
        WriteRelease("2.1.3");
        WriteRelease("2.1.4");
        var service = CreateService();

        service.MarkOpenedForCurrentVersion();

        _configuration.Values[WhatsNewService.LastOpenedVersionKey].Should().Be("2.1.3");
        (await service.ShouldShowShellEntryAsync()).Should().BeFalse();

        _appMetadataService.Setup(x => x.AppVersion).Returns("2.1.4.0");
        (await service.ShouldShowShellEntryAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("2.1.3", "2.1.3")]
    [InlineData("2.1.3.0", "2.1.3")]
    [InlineData("2.1", "2.1.0")]
    public void TryNormalizeVersion_IgnoresRevision(string text, string expected)
    {
        WhatsNewRelease.TryNormalizeVersion(text, out var version).Should().BeTrue();
        version.ToString().Should().Be(expected);
    }

    private WhatsNewService CreateService() => new(_configuration, _appMetadataService.Object, _directory);

    private void WriteRelease(string version, bool starred = false)
    {
        var json = $$"""
            {
              "version": "{{version}}",
              "isStarred": {{(starred ? "true" : "false")}},
              "features": [
                { "image": "feature-{{version}}.png", "title": "Feature {{version}}", "description": "Description" },
                { "image": "untitled.png", "title": "", "description": "Dropped" }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_directory, $"{version}.json"), json);
    }

    private sealed class InMemoryConfigurationService : IConfigurationService
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Contains(string key) => Values.ContainsKey(key);

        public bool Remove(string key) => Values.Remove(key);

        public void Set(string key, object value) => Values[key] = value?.ToString() ?? string.Empty;

        public T Get<T>(string key, T defaultValue = default!)
            => Values.TryGetValue(key, out var value) ? (T)Convert.ChangeType(value, typeof(T)) : defaultValue;

        public void SetRoaming(string key, object value) => Set(key, value);

        public T GetRoaming<T>(string key, T defaultValue = default!) => Get(key, defaultValue);
    }
}
