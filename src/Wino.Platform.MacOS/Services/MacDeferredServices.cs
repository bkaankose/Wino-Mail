using System.Security.Cryptography.X509Certificates;
using AppKit;
using MimeKit.Cryptography;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

// Explicit foundation boundaries. These integrations remain in the parity roadmap.
public sealed class MacStartupIntegrationService : IStartupIntegrationService
{
    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(StartupBehaviorResult.Unavailable);
    public Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
        => Task.FromResult(StartupBehaviorResult.Unavailable);
}

public sealed class MacSmimeCertificateService : ISmimeCertificateService
{
    private static PlatformNotSupportedException Deferred() => new("S/MIME presentation and Keychain certificate selection are scheduled after the OAuth/sidebar prototype.");
    public SecureMimeContext CreateContext(CancellationToken cancellationToken = default) => throw Deferred();
    public IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string? emailAddress = null, CancellationToken cancellationToken = default) => throw Deferred();
    public void ImportCertificate(string fileExtension, byte[] rawData, string? password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default) => throw Deferred();
    public void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default) => throw Deferred();
}

public sealed class MacTaskCompletionSound(IDispatcher dispatcher) : ITaskCompletionSound
{
    public void Play() => _ = dispatcher.ExecuteOnUIThread(() => NSSound.Beep());
}

public sealed class MacUserPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;
    public bool IsSystemQuietTimeActive() => false;
}
