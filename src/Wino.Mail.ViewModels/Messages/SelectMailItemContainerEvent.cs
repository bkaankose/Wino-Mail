using System;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// When listing view model manipulated the selected mail container in the UI.
/// </summary>
public record SelectMailItemContainerEvent(Guid MailUniqueId, bool ScrollToItem = false);
