namespace Wino.Mail.AppKit.Poc.ViewModels;

public interface IPocDialogService
{
    Task ShowMessageAsync(string title, string message);
}
