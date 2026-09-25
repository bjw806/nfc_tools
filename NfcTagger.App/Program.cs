using Avalonia;

namespace NfcTagger.App;

static class Program
{
    [STAThread]
    static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // also used by the XAML previewer
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
