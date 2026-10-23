// Filee's keyboard-driven screen OCR and QR workflow.

using System.Runtime.InteropServices;
using Avalonia.Threading;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Filee.App.Services;

public sealed class QuickOcrService(
    IPlatformServices platform,
    LocalOcrService ocr,
    WindowService windows,
    ILocalizer loc,
    ILogger<QuickOcrService> logger)
    : IDisposable
{
    private int _running;
    private readonly CancellationTokenSource _shutdown = new();

    public async Task StartAsync()
    {
        if (!platform.SupportsScreenSelection || Interlocked.Exchange(ref _running, 1) != 0)
            return;

        var restoreMain = false;
        CapturedScreen? capture = null;
        var cancellationToken = _shutdown.Token;
        try
        {
            restoreMain = windows.Main?.IsVisible == true;
            windows.HideMain();
            await Task.Delay(120, cancellationToken);

            var encodedCapture = await Task.Run(() =>
            {
                var frame = platform.CaptureScreenAtCursor();
                return (Metadata: frame with { Pixels = [] }, Png: EncodePng(frame));
            }, cancellationToken);
            capture = encodedCapture.Metadata;
            var fullScreenPng = encodedCapture.Png;
            var selection = await windows.SelectScreenshotRegionAsync(capture, fullScreenPng);
            fullScreenPng = [];
            if (selection is null)
            {
                if (restoreMain)
                    windows.ShowMain();
                return;
            }

            var recognition = await ocr.RecognizeAsync(selection, cancellationToken, () =>
                Dispatcher.UIThread.Post(() => windows.ShowScreenshotOcrResult(
                    capture, loc["ocr.preparing"], loc["ocr.model_preparing_detail"], null)));
            if (recognition.QrCodes.Count > 0)
            {
                var copyText = string.Join(Environment.NewLine, recognition.QrCodes.Select(qr => qr.Text));
                var displayText = recognition.QrCodes.Count == 1
                    ? $"{recognition.QrCodes[0].Format}\n{recognition.QrCodes[0].Text}"
                    : string.Join(Environment.NewLine + Environment.NewLine,
                        recognition.QrCodes.Select((qr, index) => $"{index + 1}. {qr.Format}\n{qr.Text}"));
                windows.ShowScreenshotOcrResult(capture, loc["ocr.result_qr"], displayText, copyText);
                return;
            }

            var text = recognition.Text;
            if (string.IsNullOrWhiteSpace(text))
                windows.ShowScreenshotOcrResult(capture, loc["ocr.result_empty"], loc["ocr.result_empty_detail"], null);
            else
                windows.ShowScreenshotOcrResult(capture, loc["ocr.result_text"], text, text, canTranslate: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is OperationCanceledException && _shutdown.IsCancellationRequested)
                return;
            logger.LogWarning(ex, "Screenshot OCR did not complete");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (restoreMain)
                    windows.ShowMain();
                var message = ex is OcrModelUnavailableException ? loc["ocr.model_unavailable"] : ex.Message;
                windows.ShowScreenshotOcrResult(
                    capture,
                    loc["ocr.result_error"],
                    message,
                    null);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _shutdown.Cancel();

    private static byte[] EncodePng(CapturedScreen capture)
    {
        if (capture.Stride != checked(capture.Width * 4) || capture.Pixels.Length != checked(capture.Stride * capture.Height))
            throw new InvalidDataException("The screen capture has an unsupported pixel layout.");
        using var bitmap = new SKBitmap(capture.Width, capture.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Marshal.Copy(capture.Pixels, 0, bitmap.GetPixels(), capture.Pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Filee could not encode the screen capture.");
        return data.ToArray();
    }
}
