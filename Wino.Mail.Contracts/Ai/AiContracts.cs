namespace Wino.Mail.Api.Contracts.Ai;

public sealed record SummarizeRequest(string Html);
public sealed record TranslateRequest(string Html, string TargetLanguage);
public sealed record RewriteRequest(string Html, string Instruction);
public sealed record AiTextResultDto(string Text);
public sealed record AiStatusResultDto(bool HasAiPack, string EntitlementStatus, DateTimeOffset? CurrentPeriodStartUtc, DateTimeOffset? CurrentPeriodEndUtc, int? MonthlyLimit, int? Used, int? Remaining);
