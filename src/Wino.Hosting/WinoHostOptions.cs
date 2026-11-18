using Microsoft.Extensions.DependencyInjection;

namespace Wino.Hosting;

public sealed class WinoHostOptions
{
    /// <summary>
    /// Required. An empty or missing folder that receives everything the host writes: the database, the MIME store,
    /// logs, settings and secrets. A folder an earlier host filled can be started again.
    /// </summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>
    /// The provider projects the host registers, in the apps' order. Shared services need every project in
    /// <see cref="WinoHostProviders.Required"/>, so only Gmail can be left out.
    /// </summary>
    public WinoHostProviders Providers { get; set; } = WinoHostProviders.All;

    /// <summary>
    /// Runs after the host's own registrations and before the container is built, so a registration here replaces
    /// a host-supplied contract.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

[Flags]
public enum WinoHostProviders
{
    None = 0,
    Gmail = 1,
    Outlook = 2,
    Imap = 4,
    Exchange = 8,
    Dav = 16,

    /// <summary>
    /// What shared services resolve from provider projects: the synchronization manager and mail invitations need
    /// IMAP (server test, SMTP), account setup needs Outlook (user realm) and IMAP (setup resolver), reauthentication
    /// needs Exchange (interactive sign-in), and the IMAP module and mail invitations need DAV.
    /// </summary>
    Required = Outlook | Imap | Exchange | Dav,
    All = Gmail | Required
}
