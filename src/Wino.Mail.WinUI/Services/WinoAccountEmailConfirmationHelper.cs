using System;
using System.Text.Json;
using Wino.Mail.Api.Contracts.Auth;

namespace Wino.Mail.WinUI.Services;

internal static class WinoAccountEmailConfirmationHelper
{
    public static bool IsEmailConfirmationRequiredError(string? errorCode)
        => string.Equals(errorCode, Wino.Mail.Api.Contracts.Common.ApiErrorCodes.EmailNotConfirmed, StringComparison.Ordinal) ||
           string.Equals(errorCode, Wino.Mail.Api.Contracts.Common.ApiErrorCodes.EmailConfirmationRequired, StringComparison.Ordinal);

    public static EmailConfirmationRequiredDetailsDto? Parse(JsonElement? details)
    {
        if (details is not JsonElement element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            if (!TryGetString(element, "resendConfirmationEndpoint", out var endpoint) ||
                !TryGetString(element, "resendConfirmationTicket", out var ticket) ||
                !TryGetDateTimeOffset(element, "resendAvailableAtUtc", out var resendAvailableAtUtc))
            {
                return null;
            }

            DateTimeOffset? latestSentUtc = null;
            if (element.TryGetProperty("latestConfirmationEmailSentUtc", out var latestSentElement) &&
                latestSentElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(latestSentElement.GetString(), out var latestParsed))
            {
                latestSentUtc = latestParsed;
            }

            return new EmailConfirmationRequiredDetailsDto(endpoint, ticket, latestSentUtc, resendAvailableAtUtc);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetDateTimeOffset(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParse(property.GetString(), out value);
    }
}
