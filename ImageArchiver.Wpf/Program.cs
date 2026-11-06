using Velopack;

namespace ImageArchiver.Wpf;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Must be the first thing to run: handles Velopack's install/update/uninstall hooks
        // and may exit the process. Don't swallow exceptions here.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
