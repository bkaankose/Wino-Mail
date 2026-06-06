using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// EWS-specific change processor. Extends the default with per-folder sync-state
/// tracking (added in Phase 1, following the Outlook per-folder delta pattern),
/// since EWS SyncFolderItems/SyncFolderHierarchy return opaque per-folder tokens
/// rather than a single account-level delta.
/// </summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    // Phase 1: per-folder EWS sync-state persistence methods will be added here.
}

public class ExchangeChangeProcessor : DefaultChangeProcessor, IExchangeChangeProcessor
{
    public ExchangeChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   ICalendarService calendarService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService)
        : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    {
    }
}
