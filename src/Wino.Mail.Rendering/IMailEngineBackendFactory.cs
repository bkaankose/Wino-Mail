using Wino.Renderer.Platform;

namespace Wino.Mail.Rendering;

/// <summary>
/// Makes the platform backend of the process: Direct2D and DirectWrite on Windows, Core Graphics and Core Text on the
/// Apple heads. <see cref="MailEngineHost"/> calls it once, on first use.
/// </summary>
public interface IMailEngineBackendFactory
{
    IPlatformBackend Create();

    /// <summary>One line for the log: which backend draws.</summary>
    string Describe(IPlatformBackend backend);
}
