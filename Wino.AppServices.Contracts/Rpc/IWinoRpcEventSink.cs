namespace Wino.AppServices.Contracts;

/// <summary>Recipient used by the generated messenger registry in the companion.</summary>
public interface IWinoRpcEventSink
{
    void Publish(object message);
}
