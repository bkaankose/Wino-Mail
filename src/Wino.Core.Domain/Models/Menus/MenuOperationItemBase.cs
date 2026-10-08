using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Menus;

public class MenuOperationItemBase<TOperation> : IMenuOperation where TOperation : Enum
{
    protected MenuOperationItemBase(
        TOperation operation,
        bool isEnabled,
        bool isSecondaryMenuPreferred)
    {
        Operation = operation;
        IsEnabled = isEnabled;
        Identifier = operation.ToString();
        IsSecondaryMenuPreferred = isSecondaryMenuPreferred;
    }

    public TOperation Operation { get; }

    public string Identifier { get; }

    public bool IsEnabled { get; }

    public bool IsSecondaryMenuPreferred { get; }
}
