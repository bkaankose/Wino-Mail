using System.Windows.Forms;

namespace Wino.Companion;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var singleInstance = CompanionSingleInstance.Acquire();
        if (!singleInstance.IsPrimary)
        {
            singleInstance.SignalPrimary();
            return;
        }

        using var context = new CompanionApplicationContext(singleInstance);
        Application.Run(context);
    }
}
