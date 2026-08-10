using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using MCLauncher.Services;

namespace MCLauncher;

#if DEBUG
/// <summary>Пишет ошибки привязок в консоль и лог — только в отладке.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Console.WriteLine("[BINDING] " + message);
        Log.Warn("Ошибка привязки: " + message);
    }
}
#endif

public partial class App : Application
{
    public static HttpClient Http { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ServicePointManager.DefaultConnectionLimit = 64;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        try
        {
            System.Net.ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch { }

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true
        };

        Http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MaysLauncher/1.0 (+windows)");
        Http.DefaultRequestHeaders.Accept.ParseAdd("*/*");

#if DEBUG
        // Ловим ошибки привязок, чтобы не копились незаметно
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
#endif

        LauncherPaths.EnsureAll();
        Log.Info("=== Запуск MaysLauncher ===");

        // Акцент нужно применить до построения окна, иначе DynamicResource не найдёт ключи
        try
        {
            var s = SettingsService.Load();
            if (!string.IsNullOrWhiteSpace(s.CustomThemeJson))
            {
                try { ThemeService.CustomPreset = System.Text.Json.JsonSerializer.Deserialize<ThemePreset>(s.CustomThemeJson); }
                catch { }
            }
            ThemeService.ApplyTheme(s.Theme);
            ThemeService.ApplyAccent(s.AccentColor);
        }
        catch (Exception ex) { Log.Warn("Не удалось применить тему: " + ex.Message); }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Необработанное исключение домена: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанное исключение задачи: " + args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Ошибка UI", e.Exception);
        MessageBox.Show(
            "Произошла ошибка:\n\n" + e.Exception.Message +
            "\n\nПодробности записаны в:\n" + LauncherPaths.LauncherLogFile,
            "MaysLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("=== Завершение работы ===");
        Http?.Dispose();
        base.OnExit(e);
    }
}
