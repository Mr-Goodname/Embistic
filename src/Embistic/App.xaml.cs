using System.Threading;
using System.Windows;

namespace Embistic;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\Embistic.Music.Client", out var first);
        if (!first) { MessageBox.Show("Embistic已经在运行，请从任务栏打开。", "Embistic"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成，请重试。\n" + Embistic.MainWindow.SafeMessage(args.Exception), "Embistic", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
