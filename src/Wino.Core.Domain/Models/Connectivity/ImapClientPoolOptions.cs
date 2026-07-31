using System;
using MailKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Connectivity;

public class ImapClientPoolOptions
{
    public CustomServerInformation ServerInformation { get; }
    public bool IsTestPool { get; }
    public Func<IProtocolLogger> ProtocolLoggerFactory { get; }

    protected ImapClientPoolOptions(
        CustomServerInformation serverInformation,
        bool isTestPool,
        Func<IProtocolLogger> protocolLoggerFactory)
    {
        ServerInformation = serverInformation;
        IsTestPool = isTestPool;
        ProtocolLoggerFactory = protocolLoggerFactory;
    }

    public static ImapClientPoolOptions CreateDefault(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null)
        => new(serverInformation, false, protocolLoggerFactory);

    public static ImapClientPoolOptions CreateTestPool(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null)
        => new(serverInformation, true, protocolLoggerFactory);
}
