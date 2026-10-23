// Filee's in-process image recognizer. QR decoding and OCR stay inside the Filee process.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RapidOcrNet;
using SkiaSharp;
using ZXing;

namespace Filee.App.Services;

public sealed class LocalOcrService : IDisposable
{
    private const string ChineseRecognitionModelFileName = "ch_PP-OCRv5_rec_mobile.onnx";
    private const string ChineseRecognitionModelAssetFileName = "ch_PP-OCRv5_rec_mobile.onnx.gz";
    private const string ChineseDictionaryFileName = "ppocrv5_dict.txt";
    private const string ChineseRecognitionModelSha256 = "5825FC7EBF84AE7A412BE049820B4D86D77620F204A041697B0494669B1742C5";
    private const string ChineseDictionarySha256 = "D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B";
    private const long MaxAssetBytes = 64 * 1024 * 1024;

    private readonly string _modelDirectory;
    private readonly ILogger<LocalOcrService> _logger;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private RapidOcr? _ocr;

    public LocalOcrService(string dataDirectory, ILogger<LocalOcrService> logger)
    {
        _modelDirectory = Path.Combine(dataDirectory, "ocr", "models", "ppocrv5");
        _logger = logger;
    }

    public bool IsReady => Volatile.Read(ref _ocr) is not null;

    internal async Task<ScreenRecognition> RecognizeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default,
        Action? ocrPreparing = null)
    {
        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            using var bitmap = DecodeImage(imageBytes);

            var qrCodes = await Task.Run(() => DecodeQrCodes(bitmap), cancellationToken);
            if (qrCodes.Count > 0)
                return new ScreenRecognition(qrCodes, "");

            if (!IsReady)
                ocrPreparing?.Invoke();
            var ocr = await GetOcrAsync(cancellationToken);
            var result = await ocr.DetectAsync(bitmap, RapidOcrOptions.Default, cancellationToken: cancellationToken);
            return new ScreenRecognition([], result.StrRes);
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    internal async Task<IReadOnlyList<DecodedQr>> DecodeQrCodesAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        using var bitmap = DecodeImage(imageBytes);
        return await Task.Run(() => DecodeQrCodes(bitmap), cancellationToken);
    }

    internal async Task<string> RecognizeTextAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default,
        Action? ocrPreparing = null)
    {
        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            using var bitmap = DecodeImage(imageBytes);
            if (!IsReady)
                ocrPreparing?.Invoke();
            var ocr = await GetOcrAsync(cancellationToken);
            var result = await ocr.DetectAsync(bitmap, RapidOcrOptions.Default, cancellationToken: cancellationToken);
            return result.StrRes;
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    private async Task<RapidOcr> GetOcrAsync(CancellationToken cancellationToken)
    {
        if (_ocr is { } ready)
            return ready;

        Directory.CreateDirectory(_modelDirectory);
        var recPath = Path.Combine(_modelDirectory, ChineseRecognitionModelFileName);
        var keysPath = Path.Combine(_modelDirectory, ChineseDictionaryFileName);
        var bundledAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr");
        var modelParts = Directory.GetFiles(bundledAssets, ChineseRecognitionModelAssetFileName + ".part*")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (modelParts.Length == 0)
            throw new OcrModelUnavailableException(new FileNotFoundException("The bundled OCR model parts are missing."));
        await EnsureCompressedAssetAsync(recPath, modelParts, ChineseRecognitionModelSha256, cancellationToken);
        await EnsureAssetAsync(keysPath, Path.Combine(bundledAssets, ChineseDictionaryFileName),
            ChineseDictionarySha256, cancellationToken);

        var instance = new RapidOcr();
        try
        {
            var latinModelRoot = Path.Combine(AppContext.BaseDirectory, "models", "v5");
            var models = RapidOcrModelSet.PPOCRv5Latin with
            {
                DetModelPath = Path.Combine(latinModelRoot, "ch_PP-OCRv5_mobile_det.onnx"),
                ClsModelPath = Path.Combine(latinModelRoot, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                RecModelPath = recPath,
                KeysPath = keysPath,
            };
            await Task.Run(() => instance.InitModels(models), cancellationToken);
            Volatile.Write(ref _ocr, instance);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    private async Task EnsureAssetAsync(string path, string bundledPath, string expectedSha256, CancellationToken cancellationToken)
    {
        if (await HasExpectedHashAsync(path, expectedSha256, cancellationToken))
            return;

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using var bundledStream = new FileStream(bundledPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (bundledStream.Length > MaxAssetBytes)
                throw new InvalidDataException("The bundled OCR model exceeds the allowed size.");
            await using Stream input = bundledPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(bundledStream, CompressionMode.Decompress, leaveOpen: true)
                : bundledStream;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaxAssetBytes)
                        throw new InvalidDataException("The bundled OCR model exceeds the allowed size.");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            var actualSha256 = Convert.ToHexString(hasher.GetHashAndReset());
            if (total == 0 || !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The bundled OCR model failed its integrity check.");

            File.Move(temporaryPath, path, overwrite: true);
            _logger.LogInformation("Installed a verified bundled OCR model asset at {Path}", path);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrModelUnavailableException(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OcrModelUnavailableException(ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not remove temporary OCR model file {Path}", temporaryPath);
            }
        }
    }

    private async Task EnsureCompressedAssetAsync(
        string path,
        IReadOnlyList<string> bundledParts,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (await HasExpectedHashAsync(path, expectedSha256, cancellationToken))
            return;

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".download";
        var compressedPath = temporaryPath + ".gz";
        try
        {
            await using (var compressedOutput = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long compressedSize = 0;
                foreach (var partPath in bundledParts)
                {
                    await using var part = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    compressedSize += part.Length;
                    if (compressedSize > MaxAssetBytes)
                        throw new InvalidDataException("The bundled OCR model exceeds the allowed size.");
                    await part.CopyToAsync(compressedOutput, cancellationToken);
                }
                await compressedOutput.FlushAsync(cancellationToken);
            }

            await using var compressedInput = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var input = new GZipStream(compressedInput, CompressionMode.Decompress);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaxAssetBytes)
                        throw new InvalidDataException("The bundled OCR model exceeds the allowed size.");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            var actualSha256 = Convert.ToHexString(hasher.GetHashAndReset());
            if (total == 0 || !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The bundled OCR model failed its integrity check.");

            File.Move(temporaryPath, path, overwrite: true);
            _logger.LogInformation("Installed a verified bundled OCR model asset at {Path}", path);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrModelUnavailableException(ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new OcrModelUnavailableException(ex);
        }
        finally
        {
            DeleteTemporary(temporaryPath);
            DeleteTemporary(compressedPath);
        }
    }

    private void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove temporary OCR model file {Path}", path);
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DecodedQr> DecodeQrCodes(SKBitmap source)
    {
        using var bitmap = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!source.CopyTo(bitmap, SKColorType.Bgra8888))
            throw new InvalidDataException("Filee could not prepare the selected image for QR recognition.");

        var stride = checked(bitmap.Width * 4);
        var pixels = new byte[checked(stride * bitmap.Height)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);

        var reader = new BarcodeReaderGeneric();
        reader.AutoRotate = true;
        reader.Options.TryHarder = true;
        reader.Options.TryInverted = true;
        reader.Options.PossibleFormats = [BarcodeFormat.QR_CODE];
        return reader.DecodeMultiple(pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32)?
            .Where(result => result.BarcodeFormat == BarcodeFormat.QR_CODE && !string.IsNullOrEmpty(result.Text))
            .Select(result => new DecodedQr(result.Text, result.BarcodeFormat.ToString()))
            .ToArray() ?? [];
    }

    private static SKBitmap DecodeImage(byte[] imageBytes)
    {
        var bitmap = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidDataException("Filee could not decode the selected image.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            bitmap.Dispose();
            throw new InvalidDataException("The selected image has invalid dimensions.");
        }
        return bitmap;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _ocr, null)?.Dispose();
        _recognitionLock.Dispose();
    }
}

internal sealed record ScreenRecognition(IReadOnlyList<DecodedQr> QrCodes, string Text);
internal sealed record DecodedQr(string Text, string Format);

internal sealed class OcrModelUnavailableException(Exception innerException)
    : Exception("Filee could not load or verify its bundled Chinese OCR model.", innerException);
