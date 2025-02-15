namespace Wino.Core.Domain.Interfaces;

public interface IMenuOperation
{
    bool IsEnabled { get; }
    string Identifier { get; }
}
