using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class PublicFolderFavoriteServiceTests
{
    private readonly InMemoryConfiguration _configuration = new();
    private readonly PublicFolderFavoriteService _service;
    private readonly Guid _accountId = Guid.NewGuid();

    public PublicFolderFavoriteServiceTests()
    {
        _service = new PublicFolderFavoriteService(_configuration);
    }

    [Fact]
    public void Favorites_AreEmptyByDefault_AndVisibilityIsOff()
    {
        _service.GetFavorites().Should().BeEmpty();
        _service.ArePublicFoldersVisible.Should().BeFalse();
        _service.AreOnlineArchivesVisible.Should().BeFalse();
    }

    [Fact]
    public void AddFavorite_PersistsOnceAndRoundTrips()
    {
        var favorite = new PublicFolderFavorite { AccountId = _accountId, FolderId = "f1", Kind = PublicFolderKind.Mail, Name = "Sales" };

        _service.AddFavorite(favorite);
        _service.AddFavorite(new PublicFolderFavorite { AccountId = _accountId, FolderId = "f1", Kind = PublicFolderKind.Mail, Name = "Duplicate" });

        var stored = new PublicFolderFavoriteService(_configuration).GetFavorites();
        stored.Should().ContainSingle();
        stored[0].Name.Should().Be("Sales");
        stored[0].Kind.Should().Be(PublicFolderKind.Mail);
        _service.IsFavorite(_accountId, "f1").Should().BeTrue();
        _service.IsFavorite(Guid.NewGuid(), "f1").Should().BeFalse();
    }

    [Fact]
    public void AddFavorite_IgnoresNullOrEmptyIds()
    {
        _service.AddFavorite(null);
        _service.AddFavorite(new PublicFolderFavorite { AccountId = _accountId, FolderId = "" });

        _service.GetFavorites().Should().BeEmpty();
    }

    [Fact]
    public void RemoveFavorite_OnlyRemovesTheMatchingEntry()
    {
        _service.AddFavorite(new PublicFolderFavorite { AccountId = _accountId, FolderId = "f1", Kind = PublicFolderKind.Mail });
        _service.AddFavorite(new PublicFolderFavorite { AccountId = _accountId, FolderId = "f2", Kind = PublicFolderKind.Mail });

        _service.RemoveFavorite(_accountId, "f1");
        _service.RemoveFavorite(_accountId, "missing");

        _service.GetFavorites().Should().ContainSingle().Which.FolderId.Should().Be("f2");
    }

    [Fact]
    public void PinningAndUnpinning_AnnounceTheKindOnce()
    {
        var recipient = new object();
        var announced = new List<PublicFolderFavoritesChanged>();
        WeakReferenceMessenger.Default.Register<PublicFolderFavoritesChanged>(recipient, (_, message) =>
        {
            if (message.AccountId == _accountId)
                announced.Add(message);
        });

        try
        {
            _service.AddFavorite(PublicFolderFavorite.Create(_accountId, "c1", PublicFolderKind.Contacts, "Staff"));
            _service.AddFavorite(PublicFolderFavorite.Create(_accountId, "c1", PublicFolderKind.Contacts, "Staff"));
            _service.RemoveFavorite(_accountId, "missing");
            _service.RemoveFavorite(_accountId, "c1");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        announced.Should().Equal(
            new PublicFolderFavoritesChanged(_accountId, PublicFolderKind.Contacts),
            new PublicFolderFavoritesChanged(_accountId, PublicFolderKind.Contacts));
    }

    [Fact]
    public void Create_GivesOnlyCalendarsAStableColour()
    {
        var calendar = PublicFolderFavorite.Create(_accountId, "cal-1", PublicFolderKind.Calendar, "Company");
        var again = PublicFolderFavorite.Create(_accountId, "cal-1", PublicFolderKind.Calendar, "Company");
        var contacts = PublicFolderFavorite.Create(_accountId, "con-1", PublicFolderKind.Contacts, "Staff");

        calendar.ColorHex.Should().MatchRegex("^#[0-9A-F]{6}$").And.Be(again.ColorHex);
        contacts.ColorHex.Should().BeNull();
        contacts.DisplayName.Should().StartWith("Staff ");
        new PublicFolderFavorite().DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DisplayName_IsNotPersisted()
    {
        _service.AddFavorite(PublicFolderFavorite.Create(_accountId, "c1", PublicFolderKind.Contacts, "Staff"));

        _configuration.Get<string>("PublicFolderFavorites").Should().NotContain("DisplayName");
    }

    [Fact]
    public void CorruptStoredJson_ReadsAsEmpty()
    {
        _configuration.Set("PublicFolderFavorites", "{not json");

        _service.GetFavorites().Should().BeEmpty();
    }

    [Fact]
    public void VisibilityToggles_Persist()
    {
        _service.ArePublicFoldersVisible = true;
        _service.AreOnlineArchivesVisible = true;

        var reread = new PublicFolderFavoriteService(_configuration);
        reread.ArePublicFoldersVisible.Should().BeTrue();
        reread.AreOnlineArchivesVisible.Should().BeTrue();
    }

    private sealed class InMemoryConfiguration : IConfigurationService
    {
        private readonly Dictionary<string, object> _values = new();

        public bool Contains(string key) => _values.ContainsKey(key);
        public bool Remove(string key) => _values.Remove(key);
        public void Set(string key, object value) => _values[key] = value;
        public T Get<T>(string key, T defaultValue = default) => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void SetRoaming(string key, object value) => Set(key, value);
        public T GetRoaming<T>(string key, T defaultValue = default) => Get(key, defaultValue);
    }
}
