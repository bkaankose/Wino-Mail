#nullable enable
using System.Text.Json;

namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountApiResult<T>
{
    public bool IsSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonElement? ErrorDetails { get; init; }
    public T? Result { get; init; }

    public static WinoAccountApiResult<T> Success(T result)
        => new()
        {
            IsSuccess = true,
            Result = result
        };

    public static WinoAccountApiResult<T> Failure(string? errorCode, string? errorMessage = null, JsonElement? errorDetails = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ErrorDetails = errorDetails
        };
}
