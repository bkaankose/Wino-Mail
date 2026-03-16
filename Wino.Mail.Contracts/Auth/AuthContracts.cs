namespace Wino.Mail.Api.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record CompleteExternalAuthRequest(string Code);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string ResetToken, string NewPassword);
public sealed record AuthUserDto(Guid UserId, string Email, string AccountStatus, bool HasPassword, bool HasGoogleLogin, bool HasFacebookLogin);
public sealed record AuthResultDto(AuthUserDto User, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);
