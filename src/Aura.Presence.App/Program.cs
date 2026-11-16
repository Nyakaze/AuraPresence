namespace Aura.Presence.App;

static class Program
{
    private const string SingleInstanceMutexName = @"Local\AuraPresence.SingleInstance";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
