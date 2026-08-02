using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class AccountProviderFeature
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public ProviderFeature Feature { get; set; }
    public ProviderFeatureAuthorizationState AuthorizationState { get; set; }
    public DateTime EnabledAtUtc { get; set; }
    public DateTime? LastAuthorizedAtUtc { get; set; }
}
