using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.SearchBar;

/// <summary>
/// Keeps inline search actions on the standard arrow cursor when hosted inside a TextBox template.
/// </summary>
public sealed partial class WinoSearchBarButton : Button
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WinoSearchBarButton"/> class.
    /// </summary>
    public WinoSearchBarButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
