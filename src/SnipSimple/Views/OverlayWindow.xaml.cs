using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SnipSimple.Helpers;
using SnipSimple.Models;
using SnipSimple.Services;

namespace SnipSimple.Views;

public partial class OverlayWindow : Window
{
    private readonly SnipMode _mode;
    private Point _startPoint;
    private bool _isDragging;
    private Rectangle? _selectionRect;
    private readonly List<WindowInfo> _windows = new();

    // Clipping rectangles for the "dark outside selection" effect
    private Rectangle? _topMask;
    private Rectangle? _bottomMask;
    private Rectangle? _leftMask;
    private Rectangle? _rightMask;

    public event Action<CaptureResult?>? CaptureCompleted;
    public event Action? CaptureCancelled;

    public OverlayWindow(SnipMode mode)
    {
        InitializeComponent();
        _mode = mode;

        // Span all screens
        var bounds = ScreenCaptureService.GetVirtualScreenBounds();
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        if (mode == SnipMode.Region)
        {
            InstructionText.Text = "Click and drag to select a region. Press ESC to cancel.";
            OverlayCanvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            OverlayCanvas.MouseMove += Canvas_MouseMove_Region;
            OverlayCanvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        }
        else if (mode == SnipMode.Window)
        {
            InstructionText.Text = "Click on a window to capture it. Press ESC to cancel.";
            _windows = WindowEnumerator.GetVisibleWindows();
            OverlayCanvas.MouseMove += Canvas_MouseMove_Window;
            OverlayCanvas.MouseLeftButtonDown += Canvas_WindowClick;
        }

        Loaded += (_, _) =>
        {
            // Position instruction text at top center
            Canvas.SetLeft(InstructionBorder, (Width - InstructionBorder.ActualWidth) / 2);
            Canvas.SetTop(InstructionBorder, 40);
            Focus();
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CaptureCancelled?.Invoke();
            Close();
        }
    }

    #region Region Selection

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(OverlayCanvas);
        _isDragging = true;

        // Create selection rectangle
        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent
        };
        OverlayCanvas.Children.Add(_selectionRect);

        // Create mask rectangles (dark overlay around selection)
        _topMask = CreateMask();
        _bottomMask = CreateMask();
        _leftMask = CreateMask();
        _rightMask = CreateMask();

        // Hide the uniform overlay, we'll use masks instead
        OverlayCanvas.Background = Brushes.Transparent;

        InstructionBorder.Visibility = Visibility.Collapsed;
        DimensionLabel.Visibility = Visibility.Visible;

        OverlayCanvas.CaptureMouse();
    }

    private Rectangle CreateMask()
    {
        var mask = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0))
        };
        OverlayCanvas.Children.Add(mask);
        return mask;
    }

    private void Canvas_MouseMove_Region(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _selectionRect == null) return;

        var currentPoint = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_startPoint.X, currentPoint.X);
        var y = Math.Min(_startPoint.Y, currentPoint.Y);
        var w = Math.Abs(currentPoint.X - _startPoint.X);
        var h = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(_selectionRect, x);
        Canvas.SetTop(_selectionRect, y);
        _selectionRect.Width = w;
        _selectionRect.Height = h;

        // Update masks
        UpdateMasks(x, y, w, h);

        // Update dimension label
        DimensionText.Text = $"{(int)w} x {(int)h}";
        Canvas.SetLeft(DimensionLabel, x);
        Canvas.SetTop(DimensionLabel, y + h + 4);
    }

    private void UpdateMasks(double x, double y, double w, double h)
    {
        if (_topMask == null) return;

        // Top mask
        Canvas.SetLeft(_topMask, 0);
        Canvas.SetTop(_topMask, 0);
        _topMask.Width = Width;
        _topMask.Height = Math.Max(0, y);

        // Bottom mask
        Canvas.SetLeft(_bottomMask!, 0);
        Canvas.SetTop(_bottomMask!, y + h);
        _bottomMask!.Width = Width;
        _bottomMask.Height = Math.Max(0, Height - y - h);

        // Left mask
        Canvas.SetLeft(_leftMask!, 0);
        Canvas.SetTop(_leftMask!, y);
        _leftMask!.Width = Math.Max(0, x);
        _leftMask.Height = Math.Max(0, h);

        // Right mask
        Canvas.SetLeft(_rightMask!, x + w);
        Canvas.SetTop(_rightMask!, y);
        _rightMask!.Width = Math.Max(0, Width - x - w);
        _rightMask.Height = Math.Max(0, h);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        OverlayCanvas.ReleaseMouseCapture();

        var endPoint = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_startPoint.X, endPoint.X);
        var y = Math.Min(_startPoint.Y, endPoint.Y);
        var w = Math.Abs(endPoint.X - _startPoint.X);
        var h = Math.Abs(endPoint.Y - _startPoint.Y);

        // Make transparent so we capture the actual screen
        Opacity = 0;

        if (w > 5 && h > 5)
        {
            // Convert from overlay coords to screen coords
            var screenX = Left + x;
            var screenY = Top + y;
            var bounds = new Rect(screenX, screenY, w, h);

            // Small delay to ensure overlay is invisible
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(50);
                var result = ScreenCaptureService.CaptureRegion(bounds);
                CaptureCompleted?.Invoke(result);
                Close();
            });
        }
        else
        {
            CaptureCancelled?.Invoke();
            Close();
        }
    }

    #endregion

    #region Window Selection

    private IntPtr _highlightedWindow = IntPtr.Zero;

    private void Canvas_MouseMove_Window(object sender, MouseEventArgs e)
    {
        var screenPoint = PointToScreen(e.GetPosition(this));
        var pt = new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y);

        // Find the window under cursor from our enumerated list
        WindowInfo? found = null;
        foreach (var win in _windows)
        {
            if (win.Bounds.Contains(screenPoint))
            {
                // Pick the smallest window that contains the point (most specific)
                if (found == null || (win.Bounds.Width * win.Bounds.Height) < (found.Bounds.Width * found.Bounds.Height))
                {
                    found = win;
                }
            }
        }

        if (found != null && found.Handle != _highlightedWindow)
        {
            _highlightedWindow = found.Handle;
            var b = found.Bounds;

            // Convert screen coords to overlay coords
            Canvas.SetLeft(WindowHighlight, b.X - Left);
            Canvas.SetTop(WindowHighlight, b.Y - Top);
            WindowHighlight.Width = b.Width;
            WindowHighlight.Height = b.Height;
            WindowHighlight.Visibility = Visibility.Visible;
        }
    }

    private void Canvas_WindowClick(object sender, MouseButtonEventArgs e)
    {
        if (_highlightedWindow == IntPtr.Zero) return;

        // Make transparent so we capture the actual screen
        Opacity = 0;

        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(50);
            var result = ScreenCaptureService.CaptureWindow(_highlightedWindow);
            CaptureCompleted?.Invoke(result);
            Close();
        });
    }

    #endregion
}
