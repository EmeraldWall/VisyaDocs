using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace VisyaDocs.App;

/// <summary>
/// Entry point. VisyaDocs runs as a single instance: opening another PDF (double click in Explorer,
/// "Open with") hands the file to the window that is already open, which shows it in a new tab.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "VisyaDocs.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // Hand the activation to the running instance, pumping COM while waiting (as Microsoft's sample does).
        nint done = CreateEventW(0, 1, 0, null);
        Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            finally
            {
                SetEvent(done);
            }
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        CloseHandle(done);
        return true;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, string? name);

    [LibraryImport("kernel32.dll")]
    private static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, nint[] handles, out uint index);
}
