namespace Wino.Hosting;

public sealed class WinoHostOptions
{
    /// <summary>
    /// Required. An empty or missing folder that receives everything the host writes: the database, the MIME store,
    /// logs, settings and secrets. A folder an earlier host filled can be started again.
    /// </summary>
    public string DataRoot { get; set; } = string.Empty;
}
