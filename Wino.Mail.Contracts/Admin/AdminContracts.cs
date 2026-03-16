namespace Wino.Mail.Api.Contracts.Admin;

public sealed record ModerateUserRequest(string ReasonCode, string? ReasonNote);
public sealed record AdminUserResultDto(Guid UserId, string Email, string AccountStatus, DateTimeOffset CreatedUtc);
public sealed record ModerationActionResultDto(string Action, string ReasonCode, string? ReasonNote, Guid? ActorUserId, DateTimeOffset CreatedUtc);
