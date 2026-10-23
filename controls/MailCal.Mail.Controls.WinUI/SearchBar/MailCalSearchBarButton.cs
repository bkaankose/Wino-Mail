using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace MailCal.Mail.Controls.SearchBar;

/// <summary>
/// Keeps inline search actions on the standard arrow cursor when hosted inside a TextBox template.
/// </summary>
public sealed partial class MailCalSearchBarButton : Button
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MailCalSearchBarButton"/> class.
    /// </summary>
    public MailCalSearchBarButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
