using System.Windows;
using System.Windows.Threading;

namespace PaintPro;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Without this any unhandled exception closes the window and takes the unsaved
        // drawing with it. A paint app has nothing to gain from dying silently: report it
        // and keep running, so the user can still save.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            "Что-то пошло не так, но приложение продолжит работу.\n" +
            "Стоит сохранить рисунок и перезапустить.\n\n" +
            e.Exception.Message,
            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
