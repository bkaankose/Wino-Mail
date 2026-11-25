using System.Runtime.InteropServices;

namespace Wino.Mail.Linux.Infrastructure.Updates;

/// <summary>
/// The package format a copy was installed from (<c>package=</c> in <see cref="Wino.Platform.Linux.LinuxDistribution"/>):
/// its key and architecture name in <c>release.json</c>, the file extension of its package, and whether the update is
/// downloaded and opened in the desktop's package installer. No desktop opens Arch packages, so an Arch copy only
/// offers the download page.
/// </summary>
internal sealed record LinuxPackageKind(string FeedKey, string Architecture, string Extension, bool DownloadsUpdate)
{
    public static LinuxPackageKind? For(string? package, Architecture architecture) => (package, architecture) switch
    {
        ("deb", System.Runtime.InteropServices.Architecture.X64) => new("deb", "amd64", ".deb", true),
        ("deb", System.Runtime.InteropServices.Architecture.Arm64) => new("deb", "arm64", ".deb", true),
        ("rpm", System.Runtime.InteropServices.Architecture.X64) => new("rpm", "x86_64", ".rpm", true),
        ("rpm", System.Runtime.InteropServices.Architecture.Arm64) => new("rpm", "aarch64", ".rpm", true),
        ("arch", System.Runtime.InteropServices.Architecture.X64) => new("arch", "x86_64", ".pkg.tar.zst", false),
        ("arch", System.Runtime.InteropServices.Architecture.Arm64) => new("arch", "aarch64", ".pkg.tar.zst", false),
        _ => null,
    };
}
