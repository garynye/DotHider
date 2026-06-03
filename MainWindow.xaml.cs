using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using ResizeMode = System.Windows.ResizeMode;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Thickness = System.Windows.Thickness;
using UIElement = System.Windows.UIElement;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Window = System.Windows.Window;
using WpfBrushes = System.Windows.Media.Brushes;
using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace DotHider;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const string DefaultTargetProcesses = "JumpDesktop,JumpClient";

    private const string SettingsJson = "settings.json";
    private const string SettingsFolder = "DotHider";
    private const uint MonitorDefaultToPrimary = 1;

    private const string DefaultMonitorAnchor = "TopRight";
    private const string DefaultMonitorShape = "Rectangle";
    private const double DefaultWidth = 48;
    private const double DefaultHeight = 36;
    private const double DefaultTopInset = 0;
    private const double DefaultRightInset = 0;
    private const string DefaultColor = "Black";

    private const int TextFieldWidth = 120;
    private const int ComboFieldWidth = 200;
    private const int MonitorSelectorWidth = 520;
    private static readonly string SettingsFilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SettingsFolder,
            SettingsJson);

    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly double Epsilon = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(
        IntPtr hMonitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private enum MonitorDpiType : int
    {
        EffectiveDpi = 0
    }

    private enum Anchor
    {
        TopRight,
        TopLeft,
        BottomRight,
        BottomLeft
    }

    private enum ShapeType
    {
        Rectangle,
        RoundedRectangle,
        Ellipse
    }

    private sealed class DotHiderSettings
    {
        public string SelectedMonitorDeviceName { get; set; } = string.Empty;
        public string Anchor { get; set; } = DefaultMonitorAnchor;
        public string Shape { get; set; } = DefaultMonitorShape;
        public double Width { get; set; } = DefaultWidth;
        public double Height { get; set; } = DefaultHeight;
        public double TopInset { get; set; } = DefaultTopInset;
        public double RightInset { get; set; } = DefaultRightInset;
        public string Color { get; set; } = DefaultColor;
        public string TargetProcessNames { get; set; } = DefaultTargetProcesses;
        public bool RequireFullscreenMatch { get; set; }
        public bool CalibrationMode { get; set; }
    }

    private sealed class MonitorSelection
    {
        public required WinForms.Screen Screen { get; init; }
        public required string Label { get; init; }
    }

    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _monitorTimer;
    private Window? _settingsWindow;
    private DotHiderSettings _settings;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _settings = LoadSettings();
        ApplyOverlayLayout();

        _monitorTimer = new DispatcherTimer { Interval = MonitorInterval };
        _monitorTimer.Tick += MonitorActiveWindow;
        _monitorTimer.Start();

        _trayIcon = BuildTrayIcon();
        Hide();
        MonitorActiveWindow(this, EventArgs.Empty);
    }

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

    private static List<MonitorSelection> GetMonitors()
    {
        var monitorInfos = WinForms.Screen.AllScreens;
        var list = new List<MonitorSelection>(monitorInfos.Length);

        for (var i = 0; i < monitorInfos.Length; i++)
        {
            var screen = monitorInfos[i];
            var role = screen.Primary ? "Primary" : "Secondary";
            var label =
                $"{role} | {screen.DeviceName} | Bounds={screen.Bounds} | WorkingArea={screen.WorkingArea}";

            list.Add(new MonitorSelection
            {
                Screen = screen,
                Label = label
            });
        }

        return list;
    }

    private WinForms.Screen GetSelectedMonitor()
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitors found.");
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedMonitorDeviceName))
        {
            var selected = monitors.FirstOrDefault(m =>
                string.Equals(m.Screen.DeviceName, _settings.SelectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected.Screen;
            }
        }

        return monitors.First(m => m.Screen.Primary).Screen;
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
        var shouldShow = ShouldShowOverlay();
        if (shouldShow)
        {
            ApplyOverlayLayout();
            if (!IsVisible)
            {
                Show();
            }

            return;
        }

        if (IsVisible)
        {
            Hide();
        }
    }

    private bool ShouldShowOverlay()
    {
        if (_settings.CalibrationMode)
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsTargetProcess(foreground))
        {
            return false;
        }

        if (_settings.RequireFullscreenMatch)
        {
            return IsForegroundWindowFullscreenOnSelectedMonitor(foreground);
        }

        return true;
    }

    private bool IsTargetProcess(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        var configuredProcesses = GetConfiguredTargetProcesses();
        if (configuredProcesses.Count == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return configuredProcesses.Contains(NormalizeProcessName(process.ProcessName));
        }
        catch
        {
            return false;
        }
    }

    private HashSet<string> GetConfiguredTargetProcesses()
    {
        if (string.IsNullOrWhiteSpace(_settings.TargetProcessNames))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            _settings.TargetProcessNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProcessName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private bool IsForegroundWindowFullscreenOnSelectedMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            return false;
        }

        var selectedMonitor = GetSelectedMonitor();
        var monitorRect = GetLogicalMonitorBounds(selectedMonitor);
        if (!TryGetMonitorScale(selectedMonitor, out var xScale, out var yScale))
        {
            xScale = 1;
            yScale = 1;
        }

        var windowWidth = (rect.Right - rect.Left) * xScale;
        var windowHeight = (rect.Bottom - rect.Top) * yScale;
        var windowLeft = rect.Left * xScale;
        var windowTop = rect.Top * yScale;
        var windowRight = windowLeft + windowWidth;
        var windowBottom = windowTop + windowHeight;

        return windowWidth + Epsilon >= monitorRect.Width &&
               windowHeight + Epsilon >= monitorRect.Height &&
               windowLeft <= monitorRect.Left + Epsilon &&
               windowTop <= monitorRect.Top + Epsilon &&
               windowRight >= monitorRect.Right - Epsilon &&
               windowBottom >= monitorRect.Bottom - Epsilon;
    }

    private static bool TryGetMonitorScale(WinForms.Screen monitor, out double xScale, out double yScale)
    {
        xScale = 1;
        yScale = 1;

        if (GetDpiForMonitor(
                MonitorFromPoint(
                    new POINT(monitor.Bounds.Left + 1, monitor.Bounds.Top + 1),
                    MonitorDefaultToPrimary),
                MonitorDpiType.EffectiveDpi,
                out var dpiX,
                out var dpiY) != 0)
        {
            return false;
        }

        xScale = 96.0 / dpiX;
        yScale = 96.0 / dpiY;
        return true;
    }

    private static Rect GetLogicalMonitorBounds(WinForms.Screen monitor)
    {
        if (!TryGetMonitorScale(monitor, out var xScale, out var yScale))
        {
            return new Rect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
        }

        return new Rect(
            monitor.Bounds.Left * xScale,
            monitor.Bounds.Top * yScale,
            monitor.Bounds.Width * xScale,
            monitor.Bounds.Height * yScale);
    }

    private void ApplyOverlayLayout()
    {
        // User-facing fields are monitor-relative DIP-ish values (Width, Height, TopInset, RightInset)
        // so the user never edits raw WPF Left/Top coordinates that vary with DPI scaling.

        var selectedMonitor = GetSelectedMonitor();
        var monitorBounds = GetLogicalMonitorBounds(selectedMonitor);

        _settings.Width = Math.Max(1, _settings.Width);
        _settings.Height = Math.Max(1, _settings.Height);
        _settings.TopInset = Math.Max(0, _settings.TopInset);
        _settings.RightInset = Math.Max(0, _settings.RightInset);

        var anchor = ParseAnchor(_settings.Anchor);
        var point = CalculateOverlayTopLeft(
            monitorBounds,
            _settings.Width,
            _settings.Height,
            _settings.TopInset,
            _settings.RightInset,
            anchor);

        Left = point.X;
        Top = point.Y;
        Width = _settings.Width;
        Height = _settings.Height;

        ConfigureShape(ParseShape(_settings.Shape), GetColorBrush(_settings.Color));
    }

    private static WpfPoint CalculateOverlayTopLeft(
        Rect monitorBounds,
        double width,
        double height,
        double topInset,
        double rightInset,
        Anchor anchor)
    {
        return anchor switch
        {
            Anchor.TopLeft => new WpfPoint(monitorBounds.Left + rightInset, monitorBounds.Top + topInset),
            Anchor.BottomRight => new WpfPoint(monitorBounds.Right - width - rightInset, monitorBounds.Bottom - height - topInset),
            Anchor.BottomLeft => new WpfPoint(monitorBounds.Left + rightInset, monitorBounds.Bottom - height - topInset),
            _ => new WpfPoint(monitorBounds.Right - width - rightInset, monitorBounds.Top + topInset)
        };
    }

    private static WpfBrush GetColorBrush(string colorText)
    {
        try
        {
            var brush = new BrushConverter().ConvertFromString(colorText);
            return brush as WpfBrush ?? WpfBrushes.Black;
        }
        catch
        {
            return WpfBrushes.Black;
        }
    }

    private void ConfigureShape(ShapeType shape, WpfBrush fill)
    {
        SetShapeVisibility(MaskRectangle, Visibility.Hidden, fill);
        SetShapeVisibility(MaskRoundedRectangle, Visibility.Hidden, fill);
        SetShapeVisibility(MaskEllipse, Visibility.Hidden, fill);

        switch (shape)
        {
            case ShapeType.RoundedRectangle:
                MaskRoundedRectangle.RadiusX = Math.Max(1, Width / 4);
                MaskRoundedRectangle.RadiusY = Math.Max(1, Height / 4);
                MaskRoundedRectangle.Visibility = Visibility.Visible;
                break;
            case ShapeType.Ellipse:
                MaskEllipse.Visibility = Visibility.Visible;
                break;
            case ShapeType.Rectangle:
            default:
                MaskRectangle.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SetShapeVisibility(Shape shape, Visibility visibility, WpfBrush fill)
    {
        shape.Visibility = visibility;
        shape.Fill = fill;
        shape.Width = Width;
        shape.Height = Height;
        shape.HorizontalAlignment = HorizontalAlignment.Left;
        shape.VerticalAlignment = VerticalAlignment.Top;
    }

    private void NudgeOffset(int rightInsetDelta, int topInsetDelta, WpfTextBox topInsetInput, WpfTextBox rightInsetInput)
    {
        if (!string.Equals(_settings.Anchor, Anchor.TopRight.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.RightInset = Math.Max(0, _settings.RightInset + rightInsetDelta);
        _settings.TopInset = Math.Max(0, _settings.TopInset + topInsetDelta);
        rightInsetInput.Text = _settings.RightInset.ToString(CultureInfo.InvariantCulture);
        topInsetInput.Text = _settings.TopInset.ToString(CultureInfo.InvariantCulture);
        ApplyOverlayLayout();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        var monitorInfos = GetMonitors();

        var monitorSelector = new ComboBox { Width = MonitorSelectorWidth, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var monitorInfo in monitorInfos)
        {
            var item = new ComboBoxItem
            {
                Content = monitorInfo.Label,
                Tag = monitorInfo.Screen.DeviceName
            };

            monitorSelector.Items.Add(item);
            if (string.Equals(monitorInfo.Screen.DeviceName, _settings.SelectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                monitorSelector.SelectedItem = item;
            }
        }

        if (monitorSelector.SelectedItem == null)
        {
            var primary = monitorInfos.FirstOrDefault(m => m.Screen.Primary);
            var selectedMonitorIndex = primary is null ? 0 : monitorInfos.IndexOf(primary);
            monitorSelector.SelectedItem = monitorSelector.Items.Count > selectedMonitorIndex
                ? monitorSelector.Items[selectedMonitorIndex]
                : null;
        }

        var anchorSelector = new ComboBox { Width = ComboFieldWidth };
        foreach (var anchor in Enum.GetValues<Anchor>())
        {
            anchorSelector.Items.Add(new ComboBoxItem { Tag = anchor.ToString(), Content = anchor.ToString() });
        }

        anchorSelector.SelectedItem = anchorSelector.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.Anchor, StringComparison.OrdinalIgnoreCase))
            ?? anchorSelector.Items[0];

        var shapeSelector = new ComboBox { Width = ComboFieldWidth };
        foreach (var shape in Enum.GetValues<ShapeType>())
        {
            shapeSelector.Items.Add(new ComboBoxItem { Tag = shape.ToString(), Content = shape.ToString() });
        }

        shapeSelector.SelectedItem = shapeSelector.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.Shape, StringComparison.OrdinalIgnoreCase))
            ?? shapeSelector.Items[0];

        var widthInput = new WpfTextBox
        {
            Text = _settings.Width.ToString(CultureInfo.InvariantCulture),
            Width = TextFieldWidth,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var heightInput = new WpfTextBox
        {
            Text = _settings.Height.ToString(CultureInfo.InvariantCulture),
            Width = TextFieldWidth,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var topInsetInput = new WpfTextBox
        {
            Text = _settings.TopInset.ToString(CultureInfo.InvariantCulture),
            Width = TextFieldWidth,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var rightInsetInput = new WpfTextBox
        {
            Text = _settings.RightInset.ToString(CultureInfo.InvariantCulture),
            Width = TextFieldWidth,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var colorSelector = new ComboBox { Width = TextFieldWidth };
        foreach (var color in new[] { "Black", "White", "Red", "Green", "Blue", "Orange", "Yellow" })
        {
            colorSelector.Items.Add(new ComboBoxItem { Tag = color, Content = color });
        }

        colorSelector.SelectedItem = colorSelector.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.Color, StringComparison.OrdinalIgnoreCase))
            ?? colorSelector.Items[0];

        var processInput = new WpfTextBox
        {
            Text = _settings.TargetProcessNames,
            Width = MonitorSelectorWidth,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var calibrationCheck = new CheckBox
        {
            Content = "Calibration mode (force-show overlay while adjusting)",
            IsChecked = _settings.CalibrationMode
        };

        var fullscreenCheck = new CheckBox
        {
            Content = "Require fullscreen check on selected monitor",
            IsChecked = _settings.RequireFullscreenMatch
        };

        var content = new StackPanel
        {
            Margin = new Thickness(12)
        };
        content.Children.Add(new TextBlock { Text = "Monitor", Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(monitorSelector);
        content.Children.Add(BuildLabeledRow("Anchor", anchorSelector));
        content.Children.Add(BuildLabeledRow("Shape", shapeSelector));
        content.Children.Add(BuildLabeledRow("Color", colorSelector));
        content.Children.Add(BuildLabeledRow("Width", widthInput));
        content.Children.Add(BuildLabeledRow("Height", heightInput));
        content.Children.Add(BuildLabeledRow("TopInset", topInsetInput));
        content.Children.Add(BuildLabeledRow("RightInset", rightInsetInput));
        content.Children.Add(BuildLabeledRow("Target process names (comma-separated)", processInput));
        content.Children.Add(new TextBlock
        {
            Text = "Nudge (TopRight: Left/Right => RightInset, Up/Down => TopInset)",
            Margin = new Thickness(0, 8, 0, 4)
        });

        var nudgeSmall = new StackPanel { Orientation = Orientation.Horizontal };
        var leftSmall = new Button { Content = "Left -1", Width = 70, Margin = new Thickness(0, 0, 4, 0) };
        var rightSmall = new Button { Content = "Right +1", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
        var upSmall = new Button { Content = "Up -1", Width = 60, Margin = new Thickness(0, 0, 4, 0) };
        var downSmall = new Button { Content = "Down +1", Width = 75, Margin = new Thickness(0, 0, 4, 0) };
        nudgeSmall.Children.Add(leftSmall);
        nudgeSmall.Children.Add(rightSmall);
        nudgeSmall.Children.Add(upSmall);
        nudgeSmall.Children.Add(downSmall);
        content.Children.Add(nudgeSmall);

        var nudgeLarge = new StackPanel { Orientation = Orientation.Horizontal };
        var leftLarge = new Button { Content = "Left -10", Width = 74, Margin = new Thickness(0, 0, 4, 0) };
        var rightLarge = new Button { Content = "Right +10", Width = 75, Margin = new Thickness(0, 0, 4, 0) };
        var upLarge = new Button { Content = "Up -10", Width = 65, Margin = new Thickness(0, 0, 4, 0) };
        var downLarge = new Button { Content = "Down +10", Width = 75, Margin = new Thickness(0, 0, 4, 0) };
        nudgeLarge.Children.Add(leftLarge);
        nudgeLarge.Children.Add(rightLarge);
        nudgeLarge.Children.Add(upLarge);
        nudgeLarge.Children.Add(downLarge);
        content.Children.Add(nudgeLarge);

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var saveButton = new Button
        {
            Content = "Save",
            Width = 90,
            Height = 28,
            Margin = new Thickness(0, 12, 8, 0)
        };
        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
            Height = 28,
            Margin = new Thickness(0, 12, 0, 0)
        };

        content.Children.Add(calibrationCheck);
        content.Children.Add(fullscreenCheck);
        actionButtons.Children.Add(saveButton);
        actionButtons.Children.Add(closeButton);
        content.Children.Add(actionButtons);

        leftSmall.Click += (_, __) => NudgeOffset(1, 0, topInsetInput, rightInsetInput);
        rightSmall.Click += (_, __) => NudgeOffset(-1, 0, topInsetInput, rightInsetInput);
        upSmall.Click += (_, __) => NudgeOffset(0, -1, topInsetInput, rightInsetInput);
        downSmall.Click += (_, __) => NudgeOffset(0, 1, topInsetInput, rightInsetInput);
        leftLarge.Click += (_, __) => NudgeOffset(10, 0, topInsetInput, rightInsetInput);
        rightLarge.Click += (_, __) => NudgeOffset(-10, 0, topInsetInput, rightInsetInput);
        upLarge.Click += (_, __) => NudgeOffset(0, -10, topInsetInput, rightInsetInput);
        downLarge.Click += (_, __) => NudgeOffset(0, 10, topInsetInput, rightInsetInput);
        calibrationCheck.Checked += (_, __) =>
        {
            _settings.CalibrationMode = true;
            ApplyOverlayLayout();
            Show();
        };
        calibrationCheck.Unchecked += (_, __) =>
        {
            _settings.CalibrationMode = false;
            if (!ShouldShowOverlay())
            {
                Hide();
            }
            else
            {
                ApplyOverlayLayout();
                Show();
            }
        };

        Action applyAnchorMode = () =>
        {
            var isTopRight = string.Equals(
                ((ComboBoxItem?)anchorSelector.SelectedItem)?.Tag?.ToString(),
                Anchor.TopRight.ToString(),
                StringComparison.OrdinalIgnoreCase);

            leftSmall.IsEnabled = isTopRight;
            rightSmall.IsEnabled = isTopRight;
            upSmall.IsEnabled = isTopRight;
            downSmall.IsEnabled = isTopRight;
            leftLarge.IsEnabled = isTopRight;
            rightLarge.IsEnabled = isTopRight;
            upLarge.IsEnabled = isTopRight;
            downLarge.IsEnabled = isTopRight;
        };

        anchorSelector.SelectionChanged += (_, __) => applyAnchorMode();
        applyAnchorMode();

        saveButton.Click += (_, __) =>
        {
            if (!TryReadNumeric(widthInput, 1, out var width) ||
                !TryReadNumeric(heightInput, 1, out var height) ||
                !TryReadNumeric(topInsetInput, 0, out var topInset) ||
                !TryReadNumeric(rightInsetInput, 0, out var rightInset))
            {
                MessageBox.Show(
                    "Please enter valid numeric values.",
                    "Invalid Input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var monitor = (ComboBoxItem?)monitorSelector.SelectedItem;
            if (monitor is null)
            {
                MessageBox.Show(
                    "Please select a monitor.",
                    "Invalid Selection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var anchor = (ComboBoxItem?)anchorSelector.SelectedItem;
            var shape = (ComboBoxItem?)shapeSelector.SelectedItem;
            var color = (ComboBoxItem?)colorSelector.SelectedItem;

            _settings.SelectedMonitorDeviceName = monitor.Tag?.ToString() ?? string.Empty;
            _settings.Anchor = anchor?.Tag?.ToString() ?? DefaultMonitorAnchor;
            _settings.Shape = shape?.Tag?.ToString() ?? DefaultMonitorShape;
            _settings.Width = width;
            _settings.Height = height;
            _settings.TopInset = topInset;
            _settings.RightInset = rightInset;
            _settings.Color = color?.Tag?.ToString() ?? DefaultColor;
            _settings.TargetProcessNames = processInput.Text.Trim();
            _settings.CalibrationMode = calibrationCheck.IsChecked == true;
            _settings.RequireFullscreenMatch = fullscreenCheck.IsChecked == true;

            ApplyOverlayLayout();
            SaveSettings();
            MessageBox.Show("Settings saved.");
        };

        closeButton.Click += (_, __) => _settingsWindow?.Close();

        _settingsWindow = new Window
        {
            Title = "Adjust Settings...",
            Width = 620,
            Height = 560,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false,
            Content = content
        };

        _settingsWindow.Closed += (_, __) =>
        {
            _settingsWindow = null;
            MonitorActiveWindow(this, EventArgs.Empty);
        };

        _settingsWindow.Show();
    }

    private static StackPanel BuildLabeledRow(string label, UIElement control)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        row.Children.Add(new TextBlock { Text = label, Width = 160, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private static bool TryReadNumeric(WpfTextBox input, double min, out double value)
    {
        if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return value >= min;
        }

        value = 0;
        return false;
    }

    private static string NormalizeProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(processName).Trim();
    }

    private static Anchor ParseAnchor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Anchor.TopRight;
        }

        return Enum.TryParse(value, true, out Anchor anchor) ? anchor : Anchor.TopRight;
    }

    private static ShapeType ParseShape(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ShapeType.Rectangle;
        }

        return Enum.TryParse(value, true, out ShapeType shape) ? shape : ShapeType.Rectangle;
    }

    private static DotHiderSettings LoadSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new DotHiderSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<DotHiderSettings>(json, JsonOptions) ?? new DotHiderSettings();
        }
        catch
        {
            return new DotHiderSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Persistence is best effort. Continue running if settings cannot be written.
        }
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
        Application.Current.Shutdown();
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
