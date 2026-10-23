using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Filee.Core.Localization;
using SkiaSharp;

namespace Filee.App.Views;

public partial class ScreenshotSelectionWindow : Window
{
    private readonly byte[] _screenshotPng;
    private readonly Bitmap _screenshot;
    private readonly ILocalizer _loc;
    private Point _start;
    private bool _dragging;

    public ScreenshotSelectionWindow(byte[] screenshotPng, ILocalizer loc)
    {
        _loc = loc;
        _screenshotPng = screenshotPng;
        _screenshot = new Bitmap(new MemoryStream(screenshotPng));
        InitializeComponent();
        ScreenImage.Source = _screenshot;
        SelectionSurface.PointerPressed += OnPointerPressed;
        SelectionSurface.PointerMoved += OnPointerMoved;
        SelectionSurface.PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        Opened += (_, _) => { SelectionSurface.Focus(); };
        Closed += (_, _) => _screenshot.Dispose();
    }

    public event Action<byte[]>? SelectionCompleted;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(SelectionSurface).Properties;
        if (properties.IsRightButtonPressed)
        {
            Close();
            return;
        }
        if (!properties.IsLeftButtonPressed)
            return;

        _start = Clamp(e.GetPosition(SelectionSurface));
        _dragging = true;
        HintText.Text = _loc["ocr.selection_hint"];
        SelectionOutline.IsVisible = true;
        e.Pointer.Capture(SelectionSurface);
        UpdateSelection(_start);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging)
            UpdateSelection(Clamp(e.GetPosition(SelectionSurface)));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        var end = Clamp(e.GetPosition(SelectionSurface));
        UpdateSelection(end);
        _dragging = false;
        e.Pointer.Capture(null);
        var rect = BoundsOf(_start, end);
        if (rect.Width < 5 || rect.Height < 5)
        {
            HintText.Text = _loc["ocr.selection_too_small"];
            return;
        }

        try
        {
            SelectionCompleted?.Invoke(Crop(rect));
            Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HintText.Text = _loc["ocr.selection_failed"];
            SelectionOutline.IsVisible = false;
        }
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, SelectionSurface.Bounds.Width),
        Math.Clamp(point.Y, 0, SelectionSurface.Bounds.Height));

    private static Rect BoundsOf(Point start, Point end) => new(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private void UpdateSelection(Point end)
    {
        var rect = BoundsOf(_start, end);
        Canvas.SetLeft(SelectionOutline, rect.Left);
        Canvas.SetTop(SelectionOutline, rect.Top);
        SelectionOutline.Width = rect.Width;
        SelectionOutline.Height = rect.Height;
    }

    private byte[] Crop(Rect selection)
    {
        using var source = SKBitmap.Decode(_screenshotPng)
            ?? throw new InvalidDataException("The screen capture could not be decoded.");
        var pixels = ToSourcePixels(selection, source.Width, source.Height, SelectionSurface.Bounds.Size);
        using var image = SKImage.FromBitmap(source);
        using var cropped = image.Subset(pixels)
            ?? throw new InvalidDataException("The selected screen area could not be cropped.");
        using var data = cropped.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("The selected screen area could not be encoded.");
        return data.ToArray();
    }

    internal static SKRectI ToSourcePixels(Rect selection, int sourceWidth, int sourceHeight, Size displaySize)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || displaySize.Width <= 0 || displaySize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displaySize));
        var left = Math.Clamp((int)Math.Floor(selection.Left * sourceWidth / displaySize.Width), 0, sourceWidth - 1);
        var top = Math.Clamp((int)Math.Floor(selection.Top * sourceHeight / displaySize.Height), 0, sourceHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(selection.Right * sourceWidth / displaySize.Width), left + 1, sourceWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom * sourceHeight / displaySize.Height), top + 1, sourceHeight);
        return new SKRectI(left, top, right, bottom);
    }
}
