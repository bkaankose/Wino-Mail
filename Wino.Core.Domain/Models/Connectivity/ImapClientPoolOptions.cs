using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Connectivity;

public class ImapClientPoolOptions
{
    public CustomServerInformation ServerInformation { get; }
    public bool IsTestPool { get; }

    protected ImapClientPoolOptions(CustomServerInformation serverInformation, bool isTestPool)
    {
        ServerInformation = serverInformation;
        IsTestPool = isTestPool;
    }

    public static ImapClientPoolOptions CreateDefault(CustomServerInformation serverInformation)
        => new(serverInformation, false);

    public static ImapClientPoolOptions CreateTestPool(CustomServerInformation serverInformation)
        => new(serverInformation, true);
}
