#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core.ContextFlyout;

/// <summary>
/// Icon of a context flyout entry. The glyph is resolved by the caller from Wino's packaged icon
/// font, so this model stays free of any XAML type.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial record ContextFlyoutIcon(string Glyph, string? ForegroundHex = null);
