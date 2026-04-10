namespace Wino.Mail.WinUI.Models;

public sealed record HostedPopoutDescriptor(
    string WindowName,
    string Title,
    double Width,
    double Height,
    double MinWidth,
    double MinHeight,
    string ContentKind);
