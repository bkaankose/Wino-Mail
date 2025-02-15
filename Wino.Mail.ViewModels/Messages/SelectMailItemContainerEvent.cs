using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// When listing view model manipulated the selected mail container in the UI.
/// </summary>
public record SelectMailItemContainerEvent(MailItemViewModel SelectedMailViewModel, bool ScrollToItem = false);
