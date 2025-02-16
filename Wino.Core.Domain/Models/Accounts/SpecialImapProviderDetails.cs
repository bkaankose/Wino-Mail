using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public record SpecialImapProviderDetails(string Address, string Password, string SenderName, SpecialImapProvider SpecialImapProvider);
