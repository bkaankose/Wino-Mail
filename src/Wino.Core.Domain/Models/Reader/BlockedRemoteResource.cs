using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

public sealed record BlockedRemoteResource(string Host, string Url, BlockedResourceReason Reason);
