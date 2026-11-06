#nullable enable
namespace Wino.Core.Domain.Models.Authentication;

/// <summary>
/// The identity an interactive Exchange sign-in reported in its ID token, when the identity provider issued one.
/// It names the sign-in identity (often the UPN), which need not be the mailbox address.
/// </summary>
public sealed record ExchangeSignInResult(string? UserName, string? DisplayName);
