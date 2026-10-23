#nullable enable
namespace MailCal.Core.Domain.Models.Accounts;

/// <summary>
/// What Sign in with Apple hands the app natively. The API verifies the identity token against the bundle id and
/// exchanges the authorization code. Apple sends the name only on the first authorization, so both parts are optional.
/// </summary>
public sealed record AppleSignInCredential(string IdentityToken, string AuthorizationCode, string? GivenName = null, string? FamilyName = null);
