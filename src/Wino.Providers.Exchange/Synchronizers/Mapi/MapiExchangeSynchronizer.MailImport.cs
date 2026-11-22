namespace Wino.Providers.Exchange.Synchronizers.Mapi;

public sealed partial class MapiExchangeSynchronizer
{
    // The EWS import of the base class needs an EWS endpoint, which a MAPI over HTTP account may not have.
    public override bool SupportsMailImport => false;
}
