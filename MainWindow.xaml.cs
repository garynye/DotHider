using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DotHider;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int MinFullscreenWidth = 3840;
    private const int MinFullscreenHeight = 2160;
    private const string TargetProcessName = "JumpDesktop";

    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _monitorTimer;
    private Window? _settingsWindow;
    private double _x = 200;
    private double _y = 200;
    private double _radius = 75;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        ApplyOverlayLayout();
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += MonitorActiveWindow;
        _monitorTimer.Start();

        _trayIcon = BuildTrayIcon();
        Hide();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThroughAndNoActivateStyles();
    }

    private WinForms.NotifyIcon BuildTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem("Adjust Settings...", null, (_, __) => OpenSettingsWindow()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, __) => ExitApplication()));

        return new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DotHider",
            Visible = true,
            ContextMenuStrip = menu
        };
    }

    private void ApplyClickThroughAndNoActivateStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle |= WsExTransparent;
        exStyle |= WsExNoActivate;
        _ = SetWindowLong(hwnd, GwlExStyle, exStyle);
    }

    private void MonitorActiveWindow(object? sender, EventArgs e)
    {
        if (ShouldShowOverlay())
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else
        {
            if (IsVisible)
            {
                Hide();
            }
        }
    }

    private bool ShouldShowOverlay()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(foreground, out RECT rect))
        {
            return false;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width < MinFullscreenWidth || height < MinFullscreenHeight)
        {
            return false;
        }

        return IsTargetProcess(foreground);
    }

    private static bool IsTargetProcess(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, TargetProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        var xInput = new System.Windows.Controls.TextBox
        {
            Text = _x.ToString(CultureInfo.InvariantCulture),
            Width = 140,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var yInput = new System.Windows.Controls.TextBox
        {
            Text = _y.ToString(CultureInfo.InvariantCulture),
            Width = 140,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var radiusInput = new System.Windows.Controls.TextBox
        {
            Text = _radius.ToString(CultureInfo.InvariantCulture),
            Width = 140,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var content = new StackPanel
        {
            Margin = new Thickness(12)
        };

        content.Children.Add(BuildLabeledRow("X", xInput));
        content.Children.Add(BuildLabeledRow("Y", yInput));
        content.Children.Add(BuildLabeledRow("Radius", radiusInput));

        var saveButton = new System.Windows.Controls.Button
        {
            Content = "Save",
            Width = 90,
            Height = 28,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        saveButton.Click += (_, __) =>
        {
            if (!TryReadSettings(xInput, yInput, radiusInput, out var x, out var y, out var radius))
            {
                System.Windows.MessageBox.Show(
                    "Please enter valid numeric values for X, Y, and Radius.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _x = x;
            _y = y;
            _radius = Math.Max(1, radius);
            ApplyOverlayLayout();
        };

        content.Children.Add(saveButton);

        _settingsWindow = new Window
        {
            Title = "Adjust Settings...",
            Width = 260,
            Height = 220,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false,
            Content = content
        };

        _settingsWindow.Closed += (_, __) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private static StackPanel BuildLabeledRow(string label, System.Windows.Controls.TextBox input)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 70,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(input);
        return row;
    }

    private static bool TryParseDoubleWithInvariantFallback(string value, NumberStyles styles, out double result)
    {
        if (double.TryParse(value, styles, CultureInfo.CurrentCulture, out result))
        {
            return true;
        }

        if (double.TryParse(value, styles, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryReadSettings(
        System.Windows.Controls.TextBox xInput,
        System.Windows.Controls.TextBox yInput,
        System.Windows.Controls.TextBox radiusInput,
        out double x,
        out double y,
        out double radius)
    {
        x = 0;
        y = 0;
        radius = 1;

        var styles = NumberStyles.Float | NumberStyles.AllowThousands;

        var parsed =
            TryParseDoubleWithInvariantFallback(xInput.Text, styles, out x) &&
            TryParseDoubleWithInvariantFallback(yInput.Text, styles, out y) &&
            TryParseDoubleWithInvariantFallback(radiusInput.Text, styles, out radius);

        if (!parsed)
        {
            x = 0;
            y = 0;
            radius = 1;
            return false;
        }

        return radius > 0;
    }

    private void ApplyOverlayLayout()
    {
        Width = _radius * 2;
        Height = _radius * 2;
        Left = _x;
        Top = _y;
        MaskEllipse.Width = Width;
        MaskEllipse.Height = Height;
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _monitorTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_isExiting)
        {
            ExitApplication();
        }

        base.OnClosed(e);
    }
}
