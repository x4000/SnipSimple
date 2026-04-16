using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnipSimple.Helpers;
using SnipSimple.Models;
using SnipSimple.Services;

namespace SnipSimple.Views;

public partial class EditorWindow : Window
{
    private readonly CaptureResult _capture;
    private readonly SettingsService _settingsService;
    private readonly Stack<StrokeCollection> _redoStack = new();
    private bool _isPencilMode = true;
    private Color _pencilColor;
    private Color _highlighterColor;
    private bool _isCropMode;
    private double _highlighterSize = 20;
    private AppTheme _currentTheme = AppTheme.Dark;

    // Shift-constrained drawing state
    private bool _shiftDrawing;
    private Point _shiftDrawStart;
    private bool _shiftAxisLocked;
    private bool _shiftLockedHorizontal;
    private StylusPointCollection? _shiftPoints;

    // Crop state
    private Point _cropStart;
    private bool _cropDragging;

    // Undo framework: stores (image, strokes, canvasSize) snapshots
    private readonly Stack<CropUndoState> _cropUndoStack = new();

    private record CropUndoState(BitmapSource Image, StrokeCollection Strokes, double Width, double Height);

    // Known "default" folder names that are self-explanatory
    private static readonly HashSet<string> WellKnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pictures", "Documents", "Desktop", "Downloads", "Videos", "Music",
        "OneDrive", "Dropbox", "Google Drive"
    };

    public EditorWindow(CaptureResult capture, SettingsService settingsService)
    {
        InitializeComponent();

        _capture = capture;
        _settingsService = settingsService;

        // Apply saved editor theme (defaults to Dark)
        _currentTheme = _settingsService.Settings.EditorTheme == "Light"
            ? AppTheme.Light : AppTheme.Dark;
        ThemeService.ApplyThemeToWindow(this, _currentTheme);
        UpdateThemeToggleIcon();

        // Load colors from settings
        _pencilColor = ParseColor(_settingsService.Settings.PencilColor, Colors.Red);
        _highlighterColor = ParseColor(_settingsService.Settings.HighlighterColor, Colors.Yellow);

        // Set up image — display at 1:1 pixel mapping.
        // The image's PixelWidth is in physical screen pixels.
        // Divide by screen DPI scale to get DIPs so WPF renders it 1:1.
        CapturedImage.Source = capture.Image;
        double dipWidth = capture.Image.PixelWidth / capture.DpiScaleX;
        double dipHeight = capture.Image.PixelHeight / capture.DpiScaleY;
        CapturedImage.Width = dipWidth;
        CapturedImage.Height = dipHeight;

        AnnotationCanvas.Width = dipWidth;
        AnnotationCanvas.Height = dipHeight;

        // Set up ink canvas
        SetPencilMode();
        UpdateColorSwatches();

        // Track strokes for undo
        AnnotationCanvas.StrokeCollected += OnStrokeCollected;

        // Shift-constrained drawing: intercept at the stroke level
        AnnotationCanvas.PreviewMouseLeftButtonDown += ShiftDraw_MouseDown;
        AnnotationCanvas.PreviewMouseMove += ShiftDraw_MouseMove;
        AnnotationCanvas.PreviewMouseLeftButtonUp += ShiftDraw_MouseUp;

        // Size window to fit image without scrollbars — use DIP sizes.
        // Account for: canvas margin (20 each side = 40), scrollbar gutter (~20),
        // window chrome (~16 horiz, ~40 vert), toolbar (~42), status bar (~28).
        double extraWidth = 40 + 20 + 16;   // margins + scrollbar room + chrome
        double extraHeight = 40 + 20 + 40 + 42 + 28; // margins + scrollbar room + chrome + toolbar + statusbar
        var screenBounds = ScreenCaptureService.GetPrimaryScreenBounds();
        Width = Math.Min(dipWidth + extraWidth, screenBounds.Width * 0.9);
        Height = Math.Min(dipHeight + extraHeight, screenBounds.Height * 0.9);
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color c ? c : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void UpdateColorSwatches()
    {
        PencilColorSwatch.Background = new SolidColorBrush(_pencilColor);
        HighlighterColorSwatch.Background = new SolidColorBrush(_highlighterColor);
    }

    #region Theme Toggle

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = ThemeService.ToggleTheme(this, _currentTheme);
        _settingsService.Settings.EditorTheme = _currentTheme.ToString();
        _settingsService.Save();
        UpdateThemeToggleIcon();
    }

    private void UpdateThemeToggleIcon()
    {
        // Sun = light mode active (click to go dark), Moon = dark mode active (click to go light)
        BtnThemeToggle.Content = _currentTheme == AppTheme.Dark ? "\u263E" : "\u2600"; // ☾ or ☀
        BtnThemeToggle.ToolTip = _currentTheme == AppTheme.Dark
            ? "Switch to light theme"
            : "Switch to dark theme";
    }

    #endregion

    #region Tool Selection

    private void SetPencilMode()
    {
        _isPencilMode = true;
        _isCropMode = false;
        BtnPencil.IsChecked = true;
        BtnHighlighter.IsChecked = false;
        BtnCrop.IsChecked = false;
        TxtHighlighterSize.Visibility = Visibility.Collapsed;
        CropOverlay.Visibility = Visibility.Collapsed;

        AnnotationCanvas.EditingMode = InkCanvasEditingMode.Ink;
        AnnotationCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _pencilColor,
            Width = 3,
            Height = 3,
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = false
        };
        AnnotationCanvas.Cursor = Cursors.Cross;
    }

    private void SetHighlighterMode()
    {
        _isPencilMode = false;
        _isCropMode = false;
        BtnPencil.IsChecked = false;
        BtnHighlighter.IsChecked = true;
        BtnCrop.IsChecked = false;
        TxtHighlighterSize.Visibility = Visibility.Visible;
        TxtHighlighterSize.Text = $"{(int)_highlighterSize}px";
        CropOverlay.Visibility = Visibility.Collapsed;

        AnnotationCanvas.EditingMode = InkCanvasEditingMode.Ink;
        AnnotationCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _highlighterColor,
            Width = _highlighterSize,
            Height = _highlighterSize,
            StylusTip = StylusTip.Rectangle,
            IsHighlighter = true
        };

        UpdateHighlighterCursor();
    }

    private void UpdateHighlighterCursor()
    {
        try
        {
            var size = Math.Max(8, (int)_highlighterSize);
            var cursorSize = size + 2;
            var hotspot = cursorSize / 2;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 1);
                var fillColor = Color.FromArgb(60, _highlighterColor.R, _highlighterColor.G, _highlighterColor.B);
                dc.DrawRectangle(new SolidColorBrush(fillColor), pen,
                    new Rect(1, 1, size, size));
                dc.DrawEllipse(System.Windows.Media.Brushes.Black, null,
                    new Point(hotspot, hotspot), 1.5, 1.5);
            }

            var rtb = new RenderTargetBitmap(cursorSize, cursorSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            using var curStream = CreateCursorStream(rtb, hotspot, hotspot);
            AnnotationCanvas.Cursor = new System.Windows.Input.Cursor(curStream);
        }
        catch
        {
            AnnotationCanvas.Cursor = Cursors.Cross;
        }
    }

    private static MemoryStream CreateCursorStream(BitmapSource source, int hotspotX, int hotspotY)
    {
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(source));
        using var pngStream = new MemoryStream();
        pngEncoder.Save(pngStream);
        var pngBytes = pngStream.ToArray();

        int width = source.PixelWidth;
        int height = source.PixelHeight;

        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)hotspotX);
        writer.Write((ushort)hotspotY);
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);
        writer.Write(pngBytes);
        writer.Flush();
        ms.Position = 0;
        return ms;
    }

    private void BtnPencil_Click(object sender, RoutedEventArgs e) => SetPencilMode();
    private void BtnHighlighter_Click(object sender, RoutedEventArgs e) => SetHighlighterMode();

    private void BtnCrop_Click(object sender, RoutedEventArgs e)
    {
        _isCropMode = BtnCrop.IsChecked == true;
        BtnPencil.IsChecked = false;
        BtnHighlighter.IsChecked = false;
        TxtHighlighterSize.Visibility = Visibility.Collapsed;

        if (_isCropMode)
        {
            AnnotationCanvas.EditingMode = InkCanvasEditingMode.None;
            AnnotationCanvas.Cursor = Cursors.Cross;
            CropOverlay.Visibility = Visibility.Visible;
            CropOverlay.IsHitTestVisible = false;
            // Reset crop visuals
            HideCropGuides();
            AnnotationCanvas.MouseLeftButtonDown += Crop_MouseDown;
            AnnotationCanvas.MouseMove += Crop_MouseMove;
            AnnotationCanvas.MouseLeftButtonUp += Crop_MouseUp;
        }
        else
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            AnnotationCanvas.MouseLeftButtonDown -= Crop_MouseDown;
            AnnotationCanvas.MouseMove -= Crop_MouseMove;
            AnnotationCanvas.MouseLeftButtonUp -= Crop_MouseUp;
            SetPencilMode();
        }
    }

    #endregion

    #region Shift-Constrained Drawing

    private bool IsShiftHeld =>
        Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

    private void ShiftDraw_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCropMode) return;
        if (!IsShiftHeld) return; // Let InkCanvas handle normal drawing

        // Shift is held: we'll manually build the stroke
        _shiftDrawing = true;
        _shiftDrawStart = e.GetPosition(AnnotationCanvas);
        _shiftAxisLocked = false;
        _shiftPoints = new StylusPointCollection
        {
            new StylusPoint(_shiftDrawStart.X, _shiftDrawStart.Y)
        };

        // Set up preview line to match current tool
        var attrs = AnnotationCanvas.DefaultDrawingAttributes;
        ShiftPreviewLine.Stroke = new SolidColorBrush(attrs.IsHighlighter
            ? Color.FromArgb(128, attrs.Color.R, attrs.Color.G, attrs.Color.B)
            : attrs.Color);
        ShiftPreviewLine.StrokeThickness = Math.Max(attrs.Width, attrs.Height);
        ShiftPreviewLine.X1 = _shiftDrawStart.X;
        ShiftPreviewLine.Y1 = _shiftDrawStart.Y;
        ShiftPreviewLine.X2 = _shiftDrawStart.X;
        ShiftPreviewLine.Y2 = _shiftDrawStart.Y;
        ShiftDrawOverlay.Visibility = Visibility.Visible;

        // Suppress InkCanvas's own stroke collection
        AnnotationCanvas.EditingMode = InkCanvasEditingMode.None;
        AnnotationCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void ShiftDraw_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_shiftDrawing || _shiftPoints == null) return;

        var pos = e.GetPosition(AnnotationCanvas);

        if (!_shiftAxisLocked)
        {
            double dx = Math.Abs(pos.X - _shiftDrawStart.X);
            double dy = Math.Abs(pos.Y - _shiftDrawStart.Y);
            if (dx > 3 || dy > 3)
            {
                _shiftAxisLocked = true;
                _shiftLockedHorizontal = dx >= dy;
            }
        }

        // Constrain the point
        Point constrained;
        if (_shiftAxisLocked)
        {
            constrained = _shiftLockedHorizontal
                ? new Point(pos.X, _shiftDrawStart.Y)
                : new Point(_shiftDrawStart.X, pos.Y);
        }
        else
        {
            constrained = _shiftDrawStart; // Don't move until locked
        }

        // Keep only start + current (straight line)
        while (_shiftPoints.Count > 1)
            _shiftPoints.RemoveAt(_shiftPoints.Count - 1);
        _shiftPoints.Add(new StylusPoint(constrained.X, constrained.Y));

        // Update preview line
        ShiftPreviewLine.X2 = constrained.X;
        ShiftPreviewLine.Y2 = constrained.Y;

        e.Handled = true;
    }

    private void ShiftDraw_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_shiftDrawing || _shiftPoints == null) return;

        _shiftDrawing = false;
        AnnotationCanvas.ReleaseMouseCapture();

        // Hide preview
        ShiftDrawOverlay.Visibility = Visibility.Collapsed;

        if (_shiftPoints.Count >= 2)
        {
            var stroke = new Stroke(_shiftPoints, AnnotationCanvas.DefaultDrawingAttributes.Clone());
            AnnotationCanvas.Strokes.Add(stroke);
            _redoStack.Clear();
        }

        _shiftPoints = null;
        // Restore ink mode
        AnnotationCanvas.EditingMode = InkCanvasEditingMode.Ink;
        e.Handled = true;
    }

    #endregion

    #region Highlighter Scale (Mouse Wheel)

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isPencilMode && !_isCropMode && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0)
                _highlighterSize = Math.Min(80, _highlighterSize + 4);
            else
                _highlighterSize = Math.Max(8, _highlighterSize - 4);

            SetHighlighterMode();
            e.Handled = true;
        }
    }

    #endregion

    #region Color Picker

    private void PencilColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        var color = ShowColorDialog(_pencilColor);
        if (color != null)
        {
            _pencilColor = color.Value;
            _settingsService.Settings.PencilColor = _pencilColor.ToString();
            _settingsService.Save();
            UpdateColorSwatches();
            if (_isPencilMode) SetPencilMode();
        }
    }

    private void HighlighterColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        var color = ShowColorDialog(_highlighterColor);
        if (color != null)
        {
            _highlighterColor = color.Value;
            _settingsService.Settings.HighlighterColor = _highlighterColor.ToString();
            _settingsService.Save();
            UpdateColorSwatches();
            if (!_isPencilMode && !_isCropMode) SetHighlighterMode();
        }
    }

    private Color? ShowColorDialog(Color currentColor)
    {
        var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B),
            FullOpen = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            return Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }
        return null;
    }

    #endregion

    #region Undo / Redo

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        _redoStack.Clear();
    }

    private void Undo()
    {
        // First try undoing a crop
        if (_cropUndoStack.Count > 0 && AnnotationCanvas.Strokes.Count == 0)
        {
            var state = _cropUndoStack.Pop();
            CapturedImage.Source = state.Image;
            CapturedImage.Width = state.Width;
            CapturedImage.Height = state.Height;
            AnnotationCanvas.Width = state.Width;
            AnnotationCanvas.Height = state.Height;
            AnnotationCanvas.Strokes = state.Strokes;
            return;
        }

        // Then undo strokes
        if (AnnotationCanvas.Strokes.Count > 0)
        {
            var last = AnnotationCanvas.Strokes[^1];
            var removed = new StrokeCollection { last };
            AnnotationCanvas.Strokes.Remove(last);
            _redoStack.Push(removed);
        }
    }

    private void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var strokes = _redoStack.Pop();
            AnnotationCanvas.Strokes.Add(strokes);
        }
    }

    #endregion

    #region Crop

    private void Crop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(AnnotationCanvas);
        _cropDragging = true;
        AnnotationCanvas.CaptureMouse();
    }

    private void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging) return;

        var pos = e.GetPosition(AnnotationCanvas);
        var x = Math.Min(_cropStart.X, pos.X);
        var y = Math.Min(_cropStart.Y, pos.Y);
        var w = Math.Abs(pos.X - _cropStart.X);
        var h = Math.Abs(pos.Y - _cropStart.Y);

        double canvasW = AnnotationCanvas.Width;
        double canvasH = AnnotationCanvas.Height;

        // Clamp to canvas bounds
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Min(w, canvasW - x);
        h = Math.Min(h, canvasH - y);

        if (w > 2 && h > 2)
        {
            UpdateCropGuides(x, y, w, h, canvasW, canvasH);
        }
    }

    private void UpdateCropGuides(double x, double y, double w, double h, double canvasW, double canvasH)
    {
        // Top mask
        Canvas.SetLeft(CropMaskTop, 0); Canvas.SetTop(CropMaskTop, 0);
        CropMaskTop.Width = canvasW; CropMaskTop.Height = Math.Max(0, y);

        // Bottom mask
        Canvas.SetLeft(CropMaskBottom, 0); Canvas.SetTop(CropMaskBottom, y + h);
        CropMaskBottom.Width = canvasW; CropMaskBottom.Height = Math.Max(0, canvasH - y - h);

        // Left mask
        Canvas.SetLeft(CropMaskLeft, 0); Canvas.SetTop(CropMaskLeft, y);
        CropMaskLeft.Width = Math.Max(0, x); CropMaskLeft.Height = Math.Max(0, h);

        // Right mask
        Canvas.SetLeft(CropMaskRight, x + w); Canvas.SetTop(CropMaskRight, y);
        CropMaskRight.Width = Math.Max(0, canvasW - x - w); CropMaskRight.Height = Math.Max(0, h);

        // Selection border
        Canvas.SetLeft(CropSelectionBorder, x); Canvas.SetTop(CropSelectionBorder, y);
        CropSelectionBorder.Width = w; CropSelectionBorder.Height = h;

        // Rule-of-thirds grid
        double thirdW = w / 3; double thirdH = h / 3;
        CropGridH1.X1 = x; CropGridH1.Y1 = y + thirdH; CropGridH1.X2 = x + w; CropGridH1.Y2 = y + thirdH;
        CropGridH2.X1 = x; CropGridH2.Y1 = y + thirdH * 2; CropGridH2.X2 = x + w; CropGridH2.Y2 = y + thirdH * 2;
        CropGridV1.X1 = x + thirdW; CropGridV1.Y1 = y; CropGridV1.X2 = x + thirdW; CropGridV1.Y2 = y + h;
        CropGridV2.X1 = x + thirdW * 2; CropGridV2.Y1 = y; CropGridV2.X2 = x + thirdW * 2; CropGridV2.Y2 = y + h;

        // Dimension label
        CropDimText.Text = $"{(int)w} x {(int)h}";
        Canvas.SetLeft(CropDimLabel, x); Canvas.SetTop(CropDimLabel, y + h + 4);

        // Make everything visible
        CropMaskTop.Visibility = Visibility.Visible;
        CropMaskBottom.Visibility = Visibility.Visible;
        CropMaskLeft.Visibility = Visibility.Visible;
        CropMaskRight.Visibility = Visibility.Visible;
        CropSelectionBorder.Visibility = Visibility.Visible;
        CropGridH1.Visibility = Visibility.Visible;
        CropGridH2.Visibility = Visibility.Visible;
        CropGridV1.Visibility = Visibility.Visible;
        CropGridV2.Visibility = Visibility.Visible;
        CropDimLabel.Visibility = Visibility.Visible;
    }

    private void HideCropGuides()
    {
        CropMaskTop.Visibility = Visibility.Collapsed;
        CropMaskBottom.Visibility = Visibility.Collapsed;
        CropMaskLeft.Visibility = Visibility.Collapsed;
        CropMaskRight.Visibility = Visibility.Collapsed;
        CropSelectionBorder.Visibility = Visibility.Collapsed;
        CropGridH1.Visibility = Visibility.Collapsed;
        CropGridH2.Visibility = Visibility.Collapsed;
        CropGridV1.Visibility = Visibility.Collapsed;
        CropGridV2.Visibility = Visibility.Collapsed;
        CropDimLabel.Visibility = Visibility.Collapsed;
    }

    private void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging) return;
        _cropDragging = false;
        AnnotationCanvas.ReleaseMouseCapture();

        var endPoint = e.GetPosition(AnnotationCanvas);
        var x = Math.Min(_cropStart.X, endPoint.X);
        var y = Math.Min(_cropStart.Y, endPoint.Y);
        var w = Math.Abs(endPoint.X - _cropStart.X);
        var h = Math.Abs(endPoint.Y - _cropStart.Y);

        if (w > 5 && h > 5)
        {
            // Save state for undo
            _cropUndoStack.Push(new CropUndoState(
                (BitmapSource)CapturedImage.Source,
                AnnotationCanvas.Strokes.Clone(),
                AnnotationCanvas.Width,
                AnnotationCanvas.Height));

            var currentImage = (BitmapSource)CapturedImage.Source;

            // Convert DIP crop coords to physical pixel coords for CroppedBitmap
            double scaleX = currentImage.PixelWidth / AnnotationCanvas.Width;
            double scaleY = currentImage.PixelHeight / AnnotationCanvas.Height;
            int pixX = (int)(x * scaleX);
            int pixY = (int)(y * scaleY);
            int pixW = (int)(w * scaleX);
            int pixH = (int)(h * scaleY);

            // Clamp to image bounds
            pixW = Math.Min(pixW, currentImage.PixelWidth - pixX);
            pixH = Math.Min(pixH, currentImage.PixelHeight - pixY);

            var cropped = new CroppedBitmap(currentImage,
                new Int32Rect(pixX, pixY, pixW, pixH));

            var wb = new WriteableBitmap(cropped);
            wb.Freeze();

            // Display at 1:1 using the original capture's DPI scale
            double newDipW = wb.PixelWidth / _capture.DpiScaleX;
            double newDipH = wb.PixelHeight / _capture.DpiScaleY;

            CapturedImage.Source = wb;
            CapturedImage.Width = newDipW;
            CapturedImage.Height = newDipH;
            AnnotationCanvas.Width = newDipW;
            AnnotationCanvas.Height = newDipH;
            AnnotationCanvas.Strokes.Clear();
            _redoStack.Clear();
        }

        HideCropGuides();
        BtnCrop.IsChecked = false;

        // Unhook crop events
        CropOverlay.Visibility = Visibility.Collapsed;
        AnnotationCanvas.MouseLeftButtonDown -= Crop_MouseDown;
        AnnotationCanvas.MouseMove -= Crop_MouseMove;
        AnnotationCanvas.MouseLeftButtonUp -= Crop_MouseUp;

        SetPencilMode();
    }

    #endregion

    #region Copy / Save / Discard

    private BitmapSource GetFlattenedBitmap()
    {
        double dipWidth = AnnotationCanvas.Width;
        double dipHeight = AnnotationCanvas.Height;
        return BitmapHelper.FlattenWithAnnotations(
            (BitmapSource)CapturedImage.Source, AnnotationCanvas, dipWidth, dipHeight);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void CopyToClipboard()
    {
        var bitmap = GetFlattenedBitmap();
        ClipboardService.CopyToClipboard(bitmap);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ShowSaveMenu();
    }

    private void ShowSaveMenu()
    {
        var menu = new ContextMenu();

        foreach (var location in _settingsService.Settings.RecentSaveLocations)
        {
            var displayName = GetSmartFolderName(location);
            var fullPath = location;
            var item = new MenuItem
            {
                Header = displayName,
                ToolTip = fullPath
            };
            item.Click += (_, _) => SaveWithDialogToFolder(fullPath);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var item = new MenuItem
            {
                Header = "Pictures",
                ToolTip = pictures
            };
            item.Click += (_, _) => SaveWithDialogToFolder(pictures);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var browseItem = new MenuItem { Header = "Different location..." };
        browseItem.Click += (_, _) => SaveWithDialogToFolder(null);
        menu.Items.Add(browseItem);

        menu.PlacementTarget = BtnSave;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static string GetSmartFolderName(string folderPath)
    {
        var dirName = Path.GetFileName(folderPath);
        if (string.IsNullOrEmpty(dirName))
            return folderPath;

        if (WellKnownFolders.Contains(dirName))
            return dirName;

        var parent = Path.GetDirectoryName(folderPath);
        if (!string.IsNullOrEmpty(parent))
        {
            var parentName = Path.GetFileName(parent);
            if (!string.IsNullOrEmpty(parentName))
                return $"{parentName}\\{dirName}";
        }

        return dirName;
    }

    /// <summary>
    /// Opens a SaveFileDialog pre-navigated to the given folder (or default if null).
    /// </summary>
    private void SaveWithDialogToFolder(string? folder)
    {
        var initialDir = folder
            ?? _settingsService.UserSettings.LastSaveLocation
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var dialog = new SaveFileDialog
        {
            Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp",
            DefaultExt = ".png",
            FileName = $"snip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var bitmap = GetFlattenedBitmap();
                BitmapHelper.SaveBitmapToFile(bitmap, dialog.FileName);
                var savedFolder = Path.GetDirectoryName(dialog.FileName)!;
                _settingsService.AddRecentSaveLocation(savedFolder);
                Title = $"SnipEd - Saved to {dialog.FileName}";
                TxtStatus.Text = $"Saved: {dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnDiscard_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region Keyboard Shortcuts

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyToClipboard();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowSaveMenu();
            e.Handled = true;
        }
    }

    #endregion
}
