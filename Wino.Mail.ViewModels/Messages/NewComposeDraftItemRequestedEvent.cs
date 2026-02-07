using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// When the compose page is already active, but a different draft item is selected.
/// To not trigger navigation again and re-use existing WebView2 editor.
/// </summary>
/// <param name="MailItemViewModel">The new draft mail item to compose.</param>
public record NewComposeDraftItemRequestedEvent(MailItemViewModel MailItemViewModel);
