using System.Security.Principal;
using System.Windows;

namespace EventDeck;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _restoreRequest;
    private RegisteredWaitHandle? _restoreWait;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var name = @"Local\EventDeck." + WindowsIdentity.GetCurrent().User!.Value;
        // Create the signal first so a launch during startup cannot lose its request.
        _restoreRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Restore");
        _instanceMutex = new Mutex(false, name + ".Instance");
        try { _ownsInstance = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance)
        {
            _restoreRequest.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _restoreWait = ThreadPool.RegisterWaitForSingleObject(_restoreRequest, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(window.RestoreExistingInstance));
        }, null, Timeout.Infinite, false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _restoreWait?.Unregister(null);
        _restoreRequest?.Dispose();
        if (_ownsInstance) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
