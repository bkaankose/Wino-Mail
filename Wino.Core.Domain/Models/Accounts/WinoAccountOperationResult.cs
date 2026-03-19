#nullable enable
using System.Text.Json;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

public sealed class WinoAccountOperationResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonElement? ErrorDetails { get; init; }
    public WinoAccount? Account { get; init; }

    public static WinoAccountOperationResult Success(WinoAccount account)
        => new()
        {
            IsSuccess = true,
            Account = account
        };

    public static WinoAccountOperationResult Failure(string? errorCode, string? errorMessage = null, JsonElement? errorDetails = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ErrorDetails = errorDetails
        };
}
