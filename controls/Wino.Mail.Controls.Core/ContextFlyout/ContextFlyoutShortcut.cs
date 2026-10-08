#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core.ContextFlyout;

/// <summary>
/// Keyboard shortcut of a context flyout entry. <see cref="Key"/> and the modifier flags feed
/// <see cref="ContextFlyoutShortcutPolicy"/>; the presenter turns them into a live accelerator.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial record ContextFlyoutShortcut(
    string DisplayText,
    string Key = "",
    bool Control = false,
    bool Alt = false,
    bool Shift = false,
    bool Windows = false)
{
    public bool CanExecuteWhileFiltering()
        => !string.IsNullOrWhiteSpace(Key)
            && ContextFlyoutShortcutPolicy.CanExecuteWhileFiltering(Key, Control, Alt, Shift, Windows);
}
