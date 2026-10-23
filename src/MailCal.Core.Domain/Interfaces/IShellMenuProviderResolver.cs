using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>Resolves a mode provider only when the shell first visits that mode.</summary>
public interface IShellMenuProviderResolver
{
    IShellMenuProvider Resolve(MailCalApplicationMode mode);
}
