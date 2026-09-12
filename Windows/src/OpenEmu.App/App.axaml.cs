using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenEmu.App.Services;
using OpenEmu.App.Views;

namespace OpenEmu.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    /// <summary>ROM paths passed on the command line ("Open with OpenEmu" / file association).</summary>
    public static string[] LaunchArgs { get; private set; } = Array.Empty<string>();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = AppServices.Create();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LaunchArgs = (desktop.Args ?? Array.Empty<string>()).Where(a => !a.StartsWith("--")).ToArray();
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => Services.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
