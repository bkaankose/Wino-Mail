namespace Wino.Core.Domain.Interfaces;

public interface IKeyPressService
{
    bool IsCtrlKeyPressed();
    bool IsShiftKeyPressed();
}
