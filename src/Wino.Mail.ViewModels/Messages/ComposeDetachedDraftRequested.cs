using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// Requests the composer to be opened for a draft that is not part of the active mail listing.
/// Listing selection is cleared and the composer is hosted in the rendering frame without the
/// draft being added to the list.
/// </summary>
public record ComposeDetachedDraftRequested(MailItemViewModel Draft);
