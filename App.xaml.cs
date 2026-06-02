namespace DotHider;

public partial class App : global::System.Windows.Application
{
    protected override void OnStartup(global::System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow = new MainWindow();
        // Start invisibly; overlay visibility is controlled by foreground app checks.
    }
}
