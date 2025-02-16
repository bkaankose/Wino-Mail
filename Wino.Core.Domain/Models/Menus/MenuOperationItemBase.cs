using System;

namespace Wino.Core.Domain.Models.Menus;

public class MenuOperationItemBase<TOperation> where TOperation : Enum
{
    public MenuOperationItemBase(TOperation operation, bool isEnabled)
    {
        Operation = operation;
        IsEnabled = isEnabled;
        Identifier = operation.ToString();
    }

    public TOperation Operation { get; set; }
    public string Identifier { get; set; }
    public bool IsEnabled { get; set; }
}
