using System;

namespace Wino.Core.Domain.Enums;

[Flags]
public enum MailServerCertificateProblem
{
    None = 0,
    UntrustedRoot = 1 << 0,
    PartialChain = 1 << 1,
    HostNameMismatch = 1 << 2,
    Expired = 1 << 3,
    NotYetValid = 1 << 4,
    RevocationUnknown = 1 << 5,
    Revoked = 1 << 6,
    InvalidSignature = 1 << 7,
    NoCertificate = 1 << 8,
    UnverifiedChain = 1 << 9,
    Other = 1 << 10
}
