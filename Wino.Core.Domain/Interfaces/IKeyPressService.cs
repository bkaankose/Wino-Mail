namespace Wino.Domain.Interfaces
{
    public interface IKeyPressService
    {
        bool IsCtrlKeyPressed();
        bool IsShiftKeyPressed();
    }
}
