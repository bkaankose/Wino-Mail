namespace Wino.Core.Domain.Entities.Shared;

public interface IContactDisplayItem
{
    string DisplayName { get; }
    string Address { get; }
    AccountContact PreviewContact { get; }
}
