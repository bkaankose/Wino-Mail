using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Domain.Models.Server;

/// <summary>
/// Encapsulates responses from the Wino server.
/// Exceptions are stored separately in the Message and StackTrace properties due to serialization issues.
/// </summary>
/// <typeparam name="T">Type of the expected response.</typeparam>
public class WinoServerResponse<T>
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; }
    public T Data { get; set; }

    public static WinoServerResponse<T> CreateSuccessResponse(T data)
    {
        return new WinoServerResponse<T>
        {
            IsSuccess = true,
            Data = data
        };
    }

    public static WinoServerResponse<T> CreateErrorResponse(string message)
    {
        return new WinoServerResponse<T>
        {
            IsSuccess = false,
            Message = message
        };
    }

    public void ThrowIfFailed()
    {
        if (!IsSuccess)
            throw new WinoServerException(Message);
    }
}
