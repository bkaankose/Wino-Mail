using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Menus;

public class MenuOperationItemBase<TOperation> : ObservableObject, IMenuOperation where TOperation : Enum
{
    private TOperation _operation;
    private string _identifier = string.Empty;
    private bool _isEnabled;
    private bool _isSecondaryMenuPreferred;

    public MenuOperationItemBase(TOperation operation, bool isEnabled)
    {
        Operation = operation;
        IsEnabled = isEnabled;
        Identifier = operation.ToString();
    }

    public TOperation Operation
    {
        get => _operation;
        set
        {
            if (SetProperty(ref _operation, value))
            {
                Identifier = value.ToString();
            }
        }
    }

    public string Identifier
    {
        get => _identifier;
        protected set => SetProperty(ref _identifier, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsSecondaryMenuPreferred
    {
        get => _isSecondaryMenuPreferred;
        set => SetProperty(ref _isSecondaryMenuPreferred, value);
    }
}
