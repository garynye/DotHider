using System;
using System.Runtime.InteropServices;

namespace DotHider;

public partial class App : global::System.Windows.Application
{
    private static readonly IntPtr PerMonitorV2Context = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(global::System.Windows.StartupEventArgs e)
    {
        _ = SetProcessDpiAwarenessContext(PerMonitorV2Context);
        base.OnStartup(e);

        MainWindow = new MainWindow();
        // Start invisibly; overlay visibility is controlled by foreground app checks.
    }
}
