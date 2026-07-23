using System.Windows;
using System.Threading;

namespace AshaLive;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\ASHA.SharedAttention.Instance.v1";
    private const string ShowEventName = "Local\\ASHA.SharedAttention.Show.v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            signal.Set();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, timedOut) =>
            {
                if (timedOut) return;
                Dispatcher.BeginInvoke(RestoreMainWindow);
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
