using System.Windows;
using System.Windows.Threading;

namespace PaintPro.Tests;

/// <summary>
/// Один STA-поток со своим диспетчером на все тесты, которые поднимают настоящие
/// WPF-элементы.
///
/// Слой представления - тысяча с лишним строк, и до 1.21.0 в нём не было ни одного теста
/// именно потому, что казалось, будто без запущенного приложения его не поднять. Поднять
/// можно: <see cref="Views.CanvasView"/> строится и раскладывается без окна, достаточно
/// подсунуть ему словари ресурсов. Нужен только STA-поток - и ОДИН на всех:
/// <see cref="Application"/> в процессе бывает ровно один, и пока каждый тест заводил свой
/// поток, они дрались за него и падали через раз, каждый прогон по-разному.
///
/// Диспетчер заодно выстраивает такие тесты в очередь, так что общее состояние WPF
/// (мышь, ресурсы, счётчики) им делить не приходится.
/// </summary>
internal static class WpfRunner
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return _dispatcher;

                var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var uri in new[]
                    {
                        "pack://application:,,,/PaintPro;component/Resources/Themes.xaml",
                        "pack://application:,,,/PaintPro;component/Resources/GlassStyles.xaml",
                        "pack://application:,,,/PaintPro;component/Resources/ToolIcons.xaml",
                    })
                    {
                        app.Resources.MergedDictionaries.Add(
                            new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
                    }
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "PaintPro.Tests WPF",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait(TimeSpan.FromSeconds(30));
                return _dispatcher!;
            }
        }
    }

    /// <summary>Выполнить проверку на WPF-потоке и вернуть её отказ сюда, как есть.</summary>
    public static void Run(Action action) => Dispatcher.Invoke(action);
}
