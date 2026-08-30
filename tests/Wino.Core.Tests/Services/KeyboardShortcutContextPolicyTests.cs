using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class KeyboardShortcutContextPolicyTests
{
    [Theory]
    [InlineData(KeyboardShortcutInputContext.Compose)]
    [InlineData(KeyboardShortcutInputContext.PopOutCompose)]
    public void Compose_IsExclusiveToSend(KeyboardShortcutInputContext context)
    {
        KeyboardShortcutContextPolicy.CanExecute(
            KeyboardShortcutAction.Send,
            "ENTER",
            ModifierKeys.Control,
            context,
            true).Should().BeTrue();
        KeyboardShortcutContextPolicy.CanExecute(
            KeyboardShortcutAction.Delete,
            "DELETE",
            ModifierKeys.None,
            context,
            false).Should().BeFalse();
    }

    [Theory]
    [InlineData("DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete)]
    [InlineData("V", ModifierKeys.Control, KeyboardShortcutAction.Move)]
    [InlineData("B", ModifierKeys.Control, KeyboardShortcutAction.NewTask)]
    public void TextInput_PreservesEditingGestures(string key, ModifierKeys modifiers, KeyboardShortcutAction action)
    {
        KeyboardShortcutContextPolicy.CanExecute(
            action,
            key,
            modifiers,
            KeyboardShortcutInputContext.Tasks,
            true).Should().BeFalse();
    }

    [Fact]
    public void TextInput_AllowsNonEditingApplicationGesture()
    {
        KeyboardShortcutContextPolicy.CanExecute(
            KeyboardShortcutAction.NewTask,
            "N",
            ModifierKeys.Control,
            KeyboardShortcutInputContext.Tasks,
            true).Should().BeTrue();
    }

    [Theory]
    [InlineData(KeyboardShortcutInputContext.List)]
    [InlineData(KeyboardShortcutInputContext.Reader)]
    [InlineData(KeyboardShortcutInputContext.PopOutReader)]
    [InlineData(KeyboardShortcutInputContext.Calendar)]
    [InlineData(KeyboardShortcutInputContext.Contacts)]
    [InlineData(KeyboardShortcutInputContext.Tasks)]
    public void NonTextRootAndReaderContexts_AllowConfiguredGesture(KeyboardShortcutInputContext context)
    {
        KeyboardShortcutContextPolicy.CanExecute(
            KeyboardShortcutAction.Delete,
            "DELETE",
            ModifierKeys.None,
            context,
            false).Should().BeTrue();
    }
}
