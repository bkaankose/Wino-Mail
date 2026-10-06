using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class KeyboardShortcutPlatformDefaultsTests
{
    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Command)]
    public async Task FreshDatabase_SeedsPlatformPrimaryModifier(ModifierKeys primary)
    {
        await using var database = new InMemoryDatabaseService();
        await database.Connection.CreateTableAsync<KeyboardShortcut>();
        var service = new KeyboardShortcutService(database, new Platform(primary));
        await service.InitializeAsync();
        var send = Assert.Single(service.EnabledShortcutsSnapshot.Where(item => item.Action == KeyboardShortcutAction.Send));
        Assert.Equal(primary, send.ModifierKeys);
        Assert.Equal("ENTER", send.Key);
        Assert.All(service.EnabledShortcutsSnapshot.Where(item => item.ModifierKeys != ModifierKeys.None),
            item => Assert.True(item.ModifierKeys.HasFlag(primary)));
    }

    [Fact]
    public async Task ExistingControlBinding_IsPreservedOnMacInitialization()
    {
        await using var database = new InMemoryDatabaseService();
        await database.Connection.CreateTableAsync<KeyboardShortcut>();
        var stored = new KeyboardShortcut
        {
            Id = Guid.NewGuid(), Mode = WinoApplicationMode.Mail, Action = KeyboardShortcutAction.Send,
            Key = "ENTER", ModifierKeys = ModifierKeys.Control, IsEnabled = true, CreatedAt = DateTime.UtcNow
        };
        await database.Connection.InsertAsync(stored);
        var service = new KeyboardShortcutService(database, new Platform(ModifierKeys.Command));
        await service.InitializeAsync();
        var send = Assert.Single(service.EnabledShortcutsSnapshot.Where(item => item.Action == KeyboardShortcutAction.Send));
        Assert.Equal(stored.Id, send.Id);
        Assert.Equal(ModifierKeys.Control, send.ModifierKeys);
        await service.ResetToDefaultShortcutsAsync();
        Assert.Equal(ModifierKeys.Command,
            Assert.Single(service.EnabledShortcutsSnapshot.Where(item => item.Action == KeyboardShortcutAction.Send)).ModifierKeys);
    }

    private sealed class Platform(ModifierKeys primary) : IShortcutPlatformService
    {
        public ModifierKeys PrimaryCommandModifier => primary;
        public bool IsShiftKeyPressed() => false;
    }
}
