using System.Threading;
using System.Windows.Forms;

namespace BudsTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\BudsTray.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "BudsTray is already running. Look for the battery icon in the notification area.",
                "BudsTray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Info("BudsTray starting");

        using var context = new TrayApplicationContext();
        Application.Run(context);

        Log.Info("BudsTray stopped");
    }
}
