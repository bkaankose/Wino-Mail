namespace Wino.Mail.Api.Contracts.Common;

public sealed class ApiEnvelope<T>
{
    public bool IsSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public T? Result { get; init; }
    public QuotaInfoDto? Quota { get; init; }

    public static ApiEnvelope<T> Success(T result, QuotaInfoDto? quota = null)
        => new()
        {
            IsSuccess = true,
            Result = result,
            Quota = quota,
        };

    public static ApiEnvelope<T> Failure(string errorCode, QuotaInfoDto? quota = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            Quota = quota,
        };
}
