#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.Authentication.OAuth;

/// <summary>RFC 8414 authorization server metadata, or the matching subset of an OIDC discovery document.</summary>
public sealed class OAuthServerMetadata
{
    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; set; }

    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; set; }

    [JsonPropertyName("revocation_endpoint")]
    public string? RevocationEndpoint { get; set; }

    [JsonPropertyName("registration_endpoint")]
    public string? RegistrationEndpoint { get; set; }

    [JsonPropertyName("scopes_supported")]
    public List<string>? ScopesSupported { get; set; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public List<string>? CodeChallengeMethodsSupported { get; set; }
}

internal sealed class OAuthClientRegistrationRequest
{
    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = string.Empty;

    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; set; } = [];

    [JsonPropertyName("grant_types")]
    public List<string> GrantTypes { get; set; } = [];

    [JsonPropertyName("response_types")]
    public List<string> ResponseTypes { get; set; } = [];

    [JsonPropertyName("token_endpoint_auth_method")]
    public string TokenEndpointAuthMethod { get; set; } = string.Empty;

    [JsonPropertyName("application_type")]
    public string ApplicationType { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }
}

internal sealed class OAuthClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }
}

internal sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    // Some servers send expires_in as a string; it is parsed by hand so a bad value falls back to the default lifetime.
    [JsonPropertyName("expires_in")]
    public JsonElement ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

internal sealed class OAuthErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// The serialized token item. It records the issuer, client and endpoints that issued the token so a refresh
/// always goes to that registration, even after the account's profile changes.
/// </summary>
internal sealed class OAuthStoredToken
{
    public string Issuer { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string? RevocationEndpoint { get; set; }
    public string? Resource { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public List<string> Scopes { get; set; } = [];
}

[JsonSerializable(typeof(OAuthServerMetadata))]
[JsonSerializable(typeof(OAuthClientRegistrationRequest))]
[JsonSerializable(typeof(OAuthClientRegistrationResponse))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(OAuthStoredToken))]
internal partial class OAuthJsonContext : JsonSerializerContext;
