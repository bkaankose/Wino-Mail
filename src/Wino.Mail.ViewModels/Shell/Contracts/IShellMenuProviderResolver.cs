using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Resolves a mode provider only when the shell first visits that mode.</summary>
public interface IShellMenuProviderResolver
{
    IShellMenuProvider Resolve(WinoApplicationMode mode);
}
