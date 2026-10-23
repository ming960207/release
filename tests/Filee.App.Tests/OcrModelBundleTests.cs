using System.IO.Compression;
using System.Security.Cryptography;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Filee.App.Tests;

public sealed class OcrModelBundleTests
{
    [Theory]
    [InlineData("ppocrv5_dict.txt", "D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B")]
    public async Task Chinese_ocr_dictionary_is_bundled_and_matches_its_pinned_hash(string fileName, string expectedHash)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr", fileName);
        Assert.True(File.Exists(path), $"The Windows app package must include {fileName}.");

        await using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public async Task Chinese_ocr_model_parts_decompress_to_the_pinned_model()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr");
        using var compressed = new MemoryStream();
        var parts = Directory.GetFiles(assets, "ch_PP-OCRv5_rec_mobile.onnx.gz.part*").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(parts);
        foreach (var part in parts)
        {
            await using var stream = File.OpenRead(part);
            await stream.CopyToAsync(compressed, TestContext.Current.CancellationToken);
        }

        compressed.Position = 0;
        await using var model = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(model, TestContext.Current.CancellationToken));

        Assert.Equal("5825FC7EBF84AE7A412BE049820B4D86D77620F204A041697B0494669B1742C5", actualHash);
    }

    [Fact]
    public async Task Chinese_ocr_initializes_from_bundled_assets_without_network()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "filee-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var service = new LocalOcrService(dataDirectory, NullLogger<LocalOcrService>.Instance);
            using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);

            var result = await service.RecognizeAsync(png!.ToArray(), TestContext.Current.CancellationToken);

            Assert.True(service.IsReady);
            Assert.Empty(result.QrCodes);
            Assert.True(File.Exists(Path.Combine(dataDirectory, "ocr", "models", "ppocrv5", "ch_PP-OCRv5_rec_mobile.onnx")));
            Assert.True(File.Exists(Path.Combine(dataDirectory, "ocr", "models", "ppocrv5", "ppocrv5_dict.txt")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
