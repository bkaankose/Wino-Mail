#nullable enable
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.Windows.PushRelay;

/// <summary>
/// Source-generated JSON for the stored relay device identity (Native AOT). Wino.Platform.Windows does not reference
/// Wino.Services, so it cannot use PushRelayJsonContext; the options match it so the file format is the same.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PushRelayDeviceIdentity))]
internal sealed partial class WindowsPushRelayJsonContext : JsonSerializerContext;
