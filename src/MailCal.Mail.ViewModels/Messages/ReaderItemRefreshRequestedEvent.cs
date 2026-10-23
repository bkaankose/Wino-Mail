using MailCal.Mail.ViewModels.Data;

namespace MailCal.Mail.ViewModels.Messages;

/// <summary>
/// Requests refreshing the currently active reader page (mail rendering or compose)
/// with a different selected mail item without re-navigation.
/// </summary>
/// <param name="MailItemViewModel">The selected mail item to refresh with.</param>
public record ReaderItemRefreshRequestedEvent(MailItemViewModel MailItemViewModel);
