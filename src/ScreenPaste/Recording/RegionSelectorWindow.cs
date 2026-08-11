using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using ScreenPaste.Capture;
using ScreenPaste.Native;
using ScreenPaste.Settings;

namespace ScreenPaste.Recording;

/// <summary>
/// Full-screen overlay for choosing a rectangular recording region. Freezes a screenshot
/// as the backdrop, dims everything but the drag selection, and returns the chosen rect in
/// physical screen coordinates via <see cref="Selection"/> (DialogResult true) or cancels (Esc).
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly VirtualScreen _vs;
    private readonly Canvas _root = new();
    private readonly Path _mask = new();
    private readonly Border _selBorder;
    private readonly Border _sizeReadout;
    private readonly TextBlock _sizeText;
    private readonly Rectangle _detectBorder;

    private double _dpiScale = 1.0;
    private Point _dragStart;
    private bool _dragging;

    // Window auto-detect (same as the screenshot overlay): snap to the window/element
    // under the cursor, wheel walks outward, a plain click selects the highlighted rect.
    private List<DetectedWindow> _windows = new();
    private ElementDetector? _elements;
    private int _detectLevel;             // 0 = deepest element … N = whole window
    private int _detectCandidates = 1;    // chain length at the current hover point
    private IntPtr _hoverWindow;
    private Rect? _detectRect;            // in _root (physical px) coords, null when off-window

    /// <summary>Chosen region in physical screen coordinates; null if cancelled.</summary>
    public Int32Rect? Selection { get; private set; }

    public RegionSelectorWindow()
    {
        var screenshot = ScreenCapture.CaptureVirtualScreen(out _vs);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;

        var image = new Image
        {
            Source = screenshot,
            Width = _vs.Width,
            Height = _vs.Height,
            Stretch = Stretch.Fill,
        };
        _root.Children.Add(image);

        _mask.Fill = new SolidColorBrush(Color.FromArgb(0x73, 0x00, 0x00, 0x00));
        _mask.IsHitTestVisible = false;
        _root.Children.Add(_mask);

        // Hovered-window auto-detect outline (dashed, matching the screenshot overlay).
        _detectBorder = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xA9, 0xFC)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_detectBorder);

        _selBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xA9, 0xFC)),
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_selBorder);

        _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        _sizeReadout = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_sizeReadout);

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            Padding = new Thickness(14, 6, 14, 6),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = Loc.T("rec.selectHint"),
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };
        _root.Children.Add(hint);
        hint.Loaded += (_, _) =>
        {
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, (_vs.Width - hint.DesiredSize.Width) / 2);
            Canvas.SetTop(hint, 40);
        };

        Content = _root;

        _root.MouseLeftButtonDown += OnMouseDown;
        _root.MouseMove += OnMouseMove;
        _root.MouseLeftButtonUp += OnMouseUp;
        MouseWheel += OnDetectWheel;
        KeyDown += OnKeyDown;
        // A resolution / monitor change (common over RDP) invalidates the frozen backdrop
        // and cached bounds, so the selection would drift — cancel out cleanly.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _elements?.Cancel();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { Activate(); Focus(); };

        UpdateMask(null);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        _dpiScale = dpi <= 0 ? 1.0 : dpi / 96.0;

        // Author children in physical px; compensate for WPF's per-monitor scaling.
        _root.RenderTransform = new ScaleTransform(1.0 / _dpiScale, 1.0 / _dpiScale);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            _vs.X, _vs.Y, _vs.Width, _vs.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Snapshot windows for auto-detect (excluding our own overlay), then walk their
        // UIA trees in the background so hovering can snap to individual UI elements.
        _windows = WindowEnumerator.Enumerate(hwnd);
        _elements = new ElementDetector();
        _elements.StartScan(_windows);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_root);
        _dragging = false;
        _root.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_root);

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateDetect(p);   // idle hover: highlight the window/element under the cursor
            return;
        }

        if (!_dragging && (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3))
            _dragging = true;
        if (!_dragging) return;

        _detectBorder.Visibility = Visibility.Collapsed;   // a manual drag overrides detect
        var r = MakeRect(_dragStart, p);
        Canvas.SetLeft(_selBorder, r.X);
        Canvas.SetTop(_selBorder, r.Y);
        _selBorder.Width = r.Width;
        _selBorder.Height = r.Height;
        _selBorder.Visibility = Visibility.Visible;
        UpdateMask(r);
        ShowSizeReadout(r);
    }

    /// <summary>
    /// Hover auto-detect: snap to the deepest cached UI element under the cursor, falling
    /// back to the whole window until its UIA scan lands. The wheel walks the level outward.
    /// </summary>
    private void UpdateDetect(Point p)
    {
        int sx = _vs.X + (int)Math.Round(p.X);
        int sy = _vs.Y + (int)Math.Round(p.Y);

        var win = WindowEnumerator.HitTestWindow(_windows, sx, sy);
        if (win is { } w)
        {
            if (w.Handle != _hoverWindow)
            {
                _hoverWindow = w.Handle;
                _detectLevel = 0;   // new window: restart at the deepest element
            }

            Rect chosen;
            if (_elements != null)
            {
                var chain = _elements.CandidatesAt(w, sx, sy);
                _detectCandidates = chain.Count;
                chosen = chain[Math.Clamp(_detectLevel, 0, chain.Count - 1)];
            }
            else
            {
                _detectCandidates = 1;
                chosen = new Rect(w.Bounds.Left, w.Bounds.Top, w.Bounds.Width, w.Bounds.Height);
            }
            _detectRect = new Rect(chosen.X - _vs.X, chosen.Y - _vs.Y, chosen.Width, chosen.Height);
        }
        else
        {
            _hoverWindow = IntPtr.Zero;
            _detectCandidates = 1;
            _detectRect = null;
        }

        if (_detectRect is { } dr)
        {
            Canvas.SetLeft(_detectBorder, dr.X);
            Canvas.SetTop(_detectBorder, dr.Y);
            _detectBorder.Width = dr.Width;
            _detectBorder.Height = dr.Height;
            _detectBorder.Visibility = Visibility.Visible;
            ShowSizeReadout(dr);
        }
        else
        {
            _detectBorder.Visibility = Visibility.Collapsed;
            _sizeReadout.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Mouse wheel while hovering: cycle the detection level (element ⇄ window).</summary>
    private void OnDetectWheel(object sender, MouseWheelEventArgs e)
    {
        if (_dragging || _detectRect == null) return;
        _detectLevel = Math.Clamp(_detectLevel + (e.Delta > 0 ? 1 : -1), 0, Math.Max(0, _detectCandidates - 1));
        UpdateDetect(e.GetPosition(_root));
        e.Handled = true;
    }

    private void ShowSizeReadout(Rect r)
    {
        _sizeText.Text = $"{(int)r.Width} × {(int)r.Height}";
        double y = r.Y - 24;
        if (y < 2) y = r.Y + 4;
        Canvas.SetLeft(_sizeReadout, r.X);
        Canvas.SetTop(_sizeReadout, y);
        _sizeReadout.Visibility = Visibility.Visible;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _root.ReleaseMouseCapture();

        Rect r;
        if (_dragging)
            r = MakeRect(_dragStart, e.GetPosition(_root));
        else if (_detectRect is { } dr)
            r = dr;                                // plain click on an auto-detected window
        else
            return;                                // click on empty space: nothing to select

        if (r.Width < 8 || r.Height < 8) return;   // ignore stray clicks

        Selection = new Int32Rect(
            _vs.X + (int)Math.Round(r.X),
            _vs.Y + (int)Math.Round(r.Y),
            (int)Math.Round(r.Width),
            (int)Math.Round(r.Height));
        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
    }

    /// <summary>Display geometry changed (e.g. an RDP resize): cancel — the frozen backdrop
    /// and cached bounds no longer match the screen.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            DialogResult = false;
            Close();
        });
    }

    private void UpdateMask(Rect? selection)
    {
        var outer = new RectangleGeometry(new Rect(0, 0, _vs.Width, _vs.Height));
        if (selection is { } s)
        {
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(outer);
            group.Children.Add(new RectangleGeometry(s));
            _mask.Data = group;
        }
        else
        {
            _mask.Data = outer;
        }
    }

    private static Rect MakeRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
