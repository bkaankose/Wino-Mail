using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// Change processor for on-premises Exchange accounts, shared by the EWS and MAPI over HTTP synchronizers.
/// The mail surface is the default one.
/// </summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Persists server information a synchronizer changed at run time, such as the detected transport when a MAPI
    /// over HTTP attempt learns that the protocol is not offered.
    /// </summary>
    Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation);
}

public class ExchangeChangeProcessor : DefaultChangeProcessor, IExchangeChangeProcessor
{
    public ExchangeChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   ICalendarService calendarService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService,
                                   IContactService? contactService = null,
                                   ITaskService? taskService = null)
        : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService, contactService, taskService)
    {
    }

    public Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation)
        => AccountService.UpdateAccountCustomServerInformationAsync(serverInformation);
}
