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
}
