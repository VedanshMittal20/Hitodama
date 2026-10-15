using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Companion.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;
using Clipboard = System.Windows.Clipboard;
using TextDataFormat = System.Windows.TextDataFormat;

namespace Companion.Shell.Windows;

public partial class CircleToAskOverlay : Window, IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr windowHandle, int index, IntPtr value);

    private readonly List<Point> _lassoPoints = [];
    private readonly MicCaptureService _microphone = new();
    private readonly System.Windows.Shapes.Polyline _outerStroke = CreateStroke(
        CreateFrozenBrush(Color.FromArgb(90, 56, 189, 248)), 4);
    private readonly System.Windows.Shapes.Polyline _innerStroke = CreateStroke(
        CreateFrozenBrush(Color.FromArgb(255, 56, 189, 248)), 1.5);

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private System.Windows.Forms.Screen _screen = System.Windows.Forms.Screen.PrimaryScreen!;
    private CancellationTokenSource? _requestCancellation;
    private TaskCompletionSource? _endingCompletion;
    private bool _isCapturing;
    private bool _endingCapture;
    private bool _disposed;

    public event Action<string>? OfflineOcrExtracted;
    public string? LastOcrText { get; private set; }

    public CircleToAskOverlay()
    {
        InitializeComponent();
        InkCanvas.Children.Add(_outerStroke);
        InkCanvas.Children.Add(_innerStroke);
        SourceInitialized += OnSourceInitialized;
        SourceInitialized += (_, _) => PositionOverCurrentMonitor();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr extendedStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, GwlExStyle)
            : GetWindowLong32(windowHandle, GwlExStyle);
        IntPtr updatedStyle = new(extendedStyle.ToInt64() | WsExNoActivate);
        Marshal.SetLastPInvokeError(0);
        IntPtr previousStyle = IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, GwlExStyle, updatedStyle)
            : SetWindowLong32(windowHandle, GwlExStyle, updatedStyle);
        if (previousStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public bool IsCapturing => _isCapturing;

    public void BeginCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isCapturing)
        {
            return;
        }

        _screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        PositionOverCurrentMonitor();
        _lassoPoints.Clear();
        UpdateStroke();
        Show();
        _requestCancellation = new CancellationTokenSource();
        try
        {
            _microphone.Start(bypassVadGate: true);
            _isCapturing = true;
        }
        catch
        {
            Hide();
            _requestCancellation.Dispose();
            _requestCancellation = null;
            throw;
        }
    }

    public async Task<(byte[] Jpeg, byte[] Pcm)> EndCaptureAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_isCapturing)
        {
            throw new InvalidOperationException("Circle to Ask capture is not active.");
        }

        _isCapturing = false;
        _endingCapture = true;
        var endingCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _endingCompletion = endingCompletion;
        InkCanvas.ReleaseMouseCapture();
        Visibility = Visibility.Hidden;
        CancellationTokenSource request = _requestCancellation
            ?? throw new InvalidOperationException("Circle to Ask capture was not initialized.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            request.Token,
            cancellationToken);
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            linkedCancellation.Token.ThrowIfCancellationRequested();

            byte[] image = CaptureSelection();
            byte[] audio = await _microphone.StopRecordingAsPcmAsync(linkedCancellation.Token);
            try
            {
                LastOcrText = await ExtractTextFromImageAsync(image);
            }
            catch (Exception exception)
            {
                LastOcrText = null;
                SafeDiagnostics.WriteFailure("circle-to-ask", "optional-ocr-context", exception);
            }

            return (image, audio);
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, request))
            {
                _requestCancellation = null;
            }

            _endingCapture = false;
            _endingCompletion = null;
            request.Dispose();
            endingCompletion.TrySetResult();
        }
    }

    public async Task CancelCaptureAsync()
    {
        _requestCancellation?.Cancel();
        if (!_isCapturing)
        {
            if (!_endingCapture && _microphone.IsRecording)
            {
                await _microphone.CancelRecordingAsync();
            }

            Hide();
            if (_endingCapture && _endingCompletion is TaskCompletionSource endingCompletion)
            {
                await endingCompletion.Task;
            }

            return;
        }

        _isCapturing = false;
        InkCanvas.ReleaseMouseCapture();
        Hide();
        try
        {
            await _microphone.CancelRecordingAsync();
        }
        finally
        {
            if (_requestCancellation is CancellationTokenSource request)
            {
                _requestCancellation = null;
                request.Dispose();
            }
        }
    }

    private void PositionOverCurrentMonitor()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _screen.Bounds.Left / dpi.DpiScaleX;
        Top = _screen.Bounds.Top / dpi.DpiScaleY;
        Width = _screen.Bounds.Width / dpi.DpiScaleX;
        Height = _screen.Bounds.Height / dpi.DpiScaleY;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _lassoPoints.Clear();
        _lassoPoints.Add(e.GetPosition(InkCanvas));
        InkCanvas.CaptureMouse();
        UpdateStroke();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCapturing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point point = e.GetPosition(InkCanvas);
        if (_lassoPoints.Count == 0 || (point - _lassoPoints[^1]).Length >= 2)
        {
            _lassoPoints.Add(point);
            UpdateStroke();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => InkCanvas.ReleaseMouseCapture();

    public static async Task<string> ExtractTextFromImageAsync(byte[] imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                return string.Empty;
            }

            OcrResult result = await engine.RecognizeAsync(bitmap);
            return result.Text.Trim();
        }
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("ocr", "extract", exception);
            return string.Empty;
        }
    }

    private void UpdateStroke()
    {
        var points = new PointCollection(_lassoPoints);
        _outerStroke.Points = points;
        _innerStroke.Points = points;
    }

    private static System.Windows.Shapes.Polyline CreateStroke(Brush brush, double thickness) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        IsHitTestVisible = false
    };

    private async void OnOverlayKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await CancelCaptureAsync();
            return;
        }

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && _isCapturing)
        {
            e.Handled = true;
            await TriggerOfflineOcrAsync();
        }
    }

    private async void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isCapturing && _lassoPoints.Count >= 3)
        {
            await TriggerOfflineOcrAsync();
        }
        else
        {
            await CancelCaptureAsync();
        }
    }

    public async Task TriggerOfflineOcrAsync()
    {
        if (!_isCapturing)
        {
            return;
        }

        byte[]? image = null;
        try
        {
            Visibility = Visibility.Hidden;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            image = CaptureSelection();
            await CancelCaptureAsync();

            string recognizedText = await ExtractTextFromImageAsync(image);
            LastOcrText = recognizedText;
            OfflineOcrExtracted?.Invoke(recognizedText);
        }
        catch (Exception exception)
        {
            SafeDiagnostics.WriteFailure("circle-to-ask", "offline-ocr", exception);
        }
        finally
        {
            if (image is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
            }
        }
    }

    private byte[] CaptureSelection()
    {
        Rectangle screenBounds = _screen.Bounds;
        Rectangle crop = GetLassoBounds(screenBounds);
        if (crop.Width < 2 || crop.Height < 2)
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero || !GetWindowRect(foregroundWindow, out NativeRect windowRect))
            {
                throw new InvalidOperationException("Could not determine the active window or circled region.");
            }

            crop = Rectangle.Intersect(
                screenBounds,
                Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom));
        }

        if (crop.Width < 2 || crop.Height < 2)
        {
            throw new InvalidOperationException("The selected screen region is empty.");
        }

        using var capture = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(capture))
        {
            graphics.CopyFromScreen(crop.Left, crop.Top, 0, 0, crop.Size, CopyPixelOperation.SourceCopy);
        }

        using Bitmap resized = ResizeIfNeeded(capture);
        using var stream = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(item => item.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        resized.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private Rectangle GetLassoBounds(Rectangle screenBounds)
    {
        if (_lassoPoints.Count < 3)
        {
            return Rectangle.Empty;
        }

        double minX = _lassoPoints.Min(point => point.X);
        double minY = _lassoPoints.Min(point => point.Y);
        double maxX = _lassoPoints.Max(point => point.X);
        double maxY = _lassoPoints.Max(point => point.Y);
        Point topLeft = InkCanvas.PointToScreen(new Point(minX, minY));
        Point bottomRight = InkCanvas.PointToScreen(new Point(maxX, maxY));
        var bounds = Rectangle.FromLTRB(
            (int)Math.Floor(topLeft.X) - 24,
            (int)Math.Floor(topLeft.Y) - 24,
            (int)Math.Ceiling(bottomRight.X) + 24,
            (int)Math.Ceiling(bottomRight.Y) + 24);
        return Rectangle.Intersect(screenBounds, bounds);
    }

    private static Bitmap ResizeIfNeeded(Bitmap source)
    {
        int longestSide = Math.Max(source.Width, source.Height);
        if (longestSide <= 1280)
        {
            return (Bitmap)source.Clone();
        }

        double scale = 1280.0 / longestSide;
        var resized = new Bitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, resized.Width, resized.Height);
        return resized;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _microphone.Dispose();
    }
}
