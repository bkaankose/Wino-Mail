#nullable enable

namespace Wino.Core.Domain.Models.Navigation;

public sealed class ShellModeActivationContext
{
    public bool IsInitialActivation { get; init; }
    public bool SuppressStartupFlows { get; init; }
    public object? Parameter { get; init; }
}
