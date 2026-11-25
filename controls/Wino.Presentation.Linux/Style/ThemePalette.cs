namespace Wino.Presentation.Linux.Style;

/// <summary>
/// Surface colours that replace libadwaita's named colours, taken from a desktop theme (Omarchy). <see cref="Accent"/>
/// is the palette's accent; the theme service decides whether it becomes <see cref="ThemeCssInput.Accent"/>.
/// </summary>
public sealed record ThemePalette(
    WinoRgba Window,
    WinoRgba WindowFg,
    WinoRgba View,
    WinoRgba Headerbar,
    WinoRgba Sidebar,
    WinoRgba Card,
    WinoRgba Popover,
    WinoRgba Dialog,
    WinoRgba Accent,
    WinoRgba? Destructive = null,
    WinoRgba? Success = null,
    WinoRgba? Warning = null);
