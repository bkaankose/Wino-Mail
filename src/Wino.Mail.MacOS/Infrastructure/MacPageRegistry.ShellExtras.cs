namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Shell extras routes. The Daily briefing panel, the What's New window and the Wino Account
/// popover are presented through their presenters (ShellContracts.cs) rather than WinoPage
/// navigation, exactly as the Windows ShellWindow hosts them, so no page is registered here.
/// </summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterShellExtrasPages()
    {
    }
}
