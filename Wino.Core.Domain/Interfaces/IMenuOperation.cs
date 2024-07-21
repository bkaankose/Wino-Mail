namespace Wino.Domain.Interfaces
{
    public interface IMenuOperation
    {
        bool IsEnabled { get; }
        string Identifier { get; }
    }
}
