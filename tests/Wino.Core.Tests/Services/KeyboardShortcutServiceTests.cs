using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class KeyboardShortcutServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private KeyboardShortcutService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new KeyboardShortcutService(_databaseService);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task CtrlZ_IsReserved_ForMail()
    {
        const WinoApplicationMode mode = WinoApplicationMode.Mail;

        _service.IsReservedShortcut(mode, "Z", ModifierKeys.Control).Should().BeTrue();
        _service.IsReservedShortcut(mode, "z", ModifierKeys.Control).Should().BeTrue();
        (await _service.IsKeyCombinationInUseAsync(mode, "Z", ModifierKeys.Control)).Should().BeTrue();
        (await _service.GetShortcutForKeyAsync(mode, "Z", ModifierKeys.Control)).Should().BeNull();

        var shortcut = new KeyboardShortcut
        {
            Mode = mode,
            Key = "Z",
            ModifierKeys = ModifierKeys.Control,
            Action = KeyboardShortcutAction.Delete,
            IsEnabled = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveKeyboardShortcutAsync(shortcut));
    }

    [Fact]
    public void CtrlZ_IsNotReserved_ForOtherModifierCombinations()
    {
        _service.IsReservedShortcut(WinoApplicationMode.Mail, "Z", ModifierKeys.Control | ModifierKeys.Shift).Should().BeFalse();
        _service.IsReservedShortcut(WinoApplicationMode.Calendar, "Z", ModifierKeys.Control).Should().BeFalse();
        _service.IsReservedShortcut(WinoApplicationMode.Calendar, "Z", ModifierKeys.None).Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_SeedsEverySupportedModeWithFinalDefaults()
    {
        await _service.InitializeAsync();

        var shortcuts = (await _service.GetKeyboardShortcutsAsync()).ToList();
        shortcuts.Should().HaveCount(15);
        shortcuts.Select(item => item.Mode).Distinct().Should().BeEquivalentTo(new[]
        {
            WinoApplicationMode.Mail,
            WinoApplicationMode.Calendar,
            WinoApplicationMode.Contacts,
            WinoApplicationMode.Tasks
        });
        shortcuts.Should().Contain(item => item.Mode == WinoApplicationMode.Mail && item.Key == "A" &&
            item.ModifierKeys == (ModifierKeys.Control | ModifierKeys.Shift) && item.Action == KeyboardShortcutAction.ToggleArchive);
        shortcuts.Should().Contain(item => item.Mode == WinoApplicationMode.Mail && item.Key == "U" &&
            item.ModifierKeys == ModifierKeys.Control && item.Action == KeyboardShortcutAction.ToggleReadUnread);
        shortcuts.Should().Contain(item => item.Mode == WinoApplicationMode.Contacts && item.Key == "N" &&
            item.ModifierKeys == ModifierKeys.Control && item.Action == KeyboardShortcutAction.NewContact);
        shortcuts.Should().Contain(item => item.Mode == WinoApplicationMode.Tasks && item.Key == "N" &&
            item.ModifierKeys == ModifierKeys.Control && item.Action == KeyboardShortcutAction.NewTask);
    }

    [Fact]
    public async Task Initialize_ReplacesOnlyExactLegacyGeneratedMailSet()
    {
        var legacy = LegacyShortcuts();
        await _databaseService.Connection.InsertAllAsync(legacy, true);

        await _service.InitializeAsync();

        var mail = (await _service.GetKeyboardShortcutsAsync()).Where(item => item.Mode == WinoApplicationMode.Mail).ToList();
        mail.Should().HaveCount(9);
        mail.Should().Contain(item => item.Action == KeyboardShortcutAction.ToggleArchive && item.Key == "A" && item.ModifierKeys == (ModifierKeys.Control | ModifierKeys.Shift));
        mail.Should().NotContain(item => item.Action == KeyboardShortcutAction.ToggleArchive && item.ModifierKeys == ModifierKeys.Control);
    }

    [Fact]
    public async Task Initialize_PreservesDisabledLegacyShortcutAsCustomization()
    {
        var legacy = LegacyShortcuts();
        legacy[0].IsEnabled = false;
        await _databaseService.Connection.InsertAllAsync(legacy, true);

        await _service.InitializeAsync();

        var mail = (await _service.GetKeyboardShortcutsAsync()).Where(item => item.Mode == WinoApplicationMode.Mail).ToList();
        mail.Should().HaveCount(8);
        mail.Single(item => item.Id == legacy[0].Id).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_PreservesCustomizedModeAndSeedsMissingModes()
    {
        var custom = Shortcut(WinoApplicationMode.Mail, "F12", ModifierKeys.Control, KeyboardShortcutAction.NewMail);
        await _databaseService.Connection.InsertAsync(custom);

        await _service.InitializeAsync();

        var all = (await _service.GetKeyboardShortcutsAsync()).ToList();
        all.Where(item => item.Mode == WinoApplicationMode.Mail).Should().ContainSingle().Which.Id.Should().Be(custom.Id);
        all.Should().Contain(item => item.Mode == WinoApplicationMode.Calendar);
        all.Should().Contain(item => item.Mode == WinoApplicationMode.Contacts);
        all.Should().Contain(item => item.Mode == WinoApplicationMode.Tasks);
    }

    [Fact]
    public async Task Save_NormalizesKeyAndRejectsDuplicateGesture()
    {
        await _service.InitializeAsync();
        var first = Shortcut(WinoApplicationMode.Mail, " f12 ", ModifierKeys.Control, KeyboardShortcutAction.NewMail);

        await _service.SaveKeyboardShortcutAsync(first);

        first.Key.Should().Be("F12");
        (await _service.GetShortcutForKeyAsync(WinoApplicationMode.Mail, "f12", ModifierKeys.Control)).Should().NotBeNull();
        var duplicate = Shortcut(WinoApplicationMode.Mail, "F12", ModifierKeys.Control, KeyboardShortcutAction.Delete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveKeyboardShortcutAsync(duplicate));
    }

    [Fact]
    public async Task EnabledUpdate_RefreshesImmutableSnapshotAndRaisesNotification()
    {
        await _service.InitializeAsync();
        var shortcut = _service.EnabledShortcutsSnapshot.First();
        var originalSnapshot = _service.EnabledShortcutsSnapshot;
        var notifications = 0;
        _service.KeyboardShortcutsChanged += (_, _) => notifications++;

        await _service.UpdateKeyboardShortcutEnabledAsync(shortcut.Id, false);

        notifications.Should().Be(1);
        _service.EnabledShortcutsSnapshot.Should().NotBeSameAs(originalSnapshot);
        _service.EnabledShortcutsSnapshot.Should().NotContain(item => item.Id == shortcut.Id);
        (await _service.GetKeyboardShortcutsAsync()).Single(item => item.Id == shortcut.Id).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAndReset_RefreshSnapshotAndRestoreDefaults()
    {
        await _service.InitializeAsync();
        var shortcut = _service.EnabledShortcutsSnapshot.First();

        await _service.DeleteKeyboardShortcutAsync(shortcut.Id);
        _service.EnabledShortcutsSnapshot.Should().NotContain(item => item.Id == shortcut.Id);

        await _service.ResetToDefaultShortcutsAsync();
        _service.EnabledShortcutsSnapshot.Should().HaveCount(15);
    }

    [Fact]
    public void WindowsGesturesAndUnsafeSendBindings_AreRejected()
    {
        _service.IsReservedShortcut(WinoApplicationMode.Tasks, "N", ModifierKeys.Windows).Should().BeTrue();
        _service.IsShortcutAllowed(Shortcut(WinoApplicationMode.Mail, "Enter", ModifierKeys.None, KeyboardShortcutAction.Send)).Should().BeFalse();
        _service.IsShortcutAllowed(Shortcut(WinoApplicationMode.Mail, "V", ModifierKeys.Control, KeyboardShortcutAction.Send)).Should().BeFalse();
        _service.IsShortcutAllowed(Shortcut(WinoApplicationMode.Mail, "Enter", ModifierKeys.Control, KeyboardShortcutAction.Send)).Should().BeTrue();
    }

    private static KeyboardShortcut Shortcut(
        WinoApplicationMode mode,
        string key,
        ModifierKeys modifiers,
        KeyboardShortcutAction action)
        => new()
        {
            Id = Guid.NewGuid(),
            Mode = mode,
            Key = key,
            ModifierKeys = modifiers,
            Action = action,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

    private static KeyboardShortcut[] LegacyShortcuts()
        =>
        [
            Shortcut(WinoApplicationMode.Mail, "Delete", ModifierKeys.None, KeyboardShortcutAction.Delete),
            Shortcut(WinoApplicationMode.Mail, "N", ModifierKeys.Control, KeyboardShortcutAction.NewMail),
            Shortcut(WinoApplicationMode.Mail, "A", ModifierKeys.Control, KeyboardShortcutAction.ToggleArchive),
            Shortcut(WinoApplicationMode.Mail, "R", ModifierKeys.Control, KeyboardShortcutAction.ToggleReadUnread),
            Shortcut(WinoApplicationMode.Mail, "F", ModifierKeys.Control, KeyboardShortcutAction.ToggleFlag),
            Shortcut(WinoApplicationMode.Mail, "M", ModifierKeys.Control, KeyboardShortcutAction.Move),
            Shortcut(WinoApplicationMode.Mail, "R", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.ReplyAll),
            Shortcut(WinoApplicationMode.Mail, "Enter", ModifierKeys.Control, KeyboardShortcutAction.Send)
        ];
}
