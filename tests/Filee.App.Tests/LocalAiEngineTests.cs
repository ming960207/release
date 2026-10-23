using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Filee.App.Tests;

public sealed class LocalAiEngineTests
{
    [Fact]
    public void Recognition_and_translation_are_registered_as_conversion_routes()
    {
        using var ocr = new LocalOcrService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            NullLogger<LocalOcrService>.Instance);
        using var translation = new OfflineTranslationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            NullLogger<OfflineTranslationService>.Instance);
        var qrConverter = new QrCodeConverter(ocr);
        var ocrConverter = new OcrConverter(ocr);
        var translationConverter = new OfflineTranslationConverter(translation);
        var planner = new RoutePlanner([qrConverter, ocrConverter, translationConverter]);

        Assert.NotNull(planner.Plan("png", "qrtext"));
        Assert.NotNull(planner.Plan("png", "ocrtext"));
        Assert.NotNull(planner.Plan("txt", OfflineTranslationService.EnglishTargetFormat));
        Assert.NotNull(planner.Plan("txt", OfflineTranslationService.ChineseTargetFormat));
        Assert.NotNull(planner.Plan("png", OfflineTranslationService.EnglishTargetFormat));
        Assert.NotNull(planner.Plan("png", OfflineTranslationService.ChineseTargetFormat));
        Assert.Equal("txt", FormatRegistry.Get(OfflineTranslationService.EnglishTargetFormat).PrimaryExtension);
        Assert.Equal("txt", FormatRegistry.Get("ocrtext").PrimaryExtension);
        Assert.Equal("txt", FormatRegistry.Get("qrtext").PrimaryExtension);
        Assert.Contains(BuiltInData.CreatePresets(), preset => preset.Id == "recognize-text");
        Assert.Contains(BuiltInData.CreatePresets(), preset => preset.Id == "extract-qr");
        Assert.Contains(BuiltInData.CreatePresets(), preset => preset.Id == "translate-to-en");
        Assert.Contains(BuiltInData.CreatePresets(), preset => preset.Id == "translate-to-zh");
    }

    [Fact]
    public void Translation_model_package_is_on_demand_and_has_pinned_raw_assets_on_windows()
    {
        if (EngineDownloads.RuntimeIdentifier != "win-x64")
            return;

        var package = EngineDownloads.Packages.Single(p => p.Id == OfflineTranslationConverter.ConverterId);

        Assert.Equal(10, package.Components.Count);
        Assert.Equal(326_107_275, EngineDownloads.DownloadSize(package));
        Assert.False(EngineDownloads.IsSuggested(package));
        Assert.False(package.AllowOwnCopy);
        Assert.All(package.Components, id =>
        {
            var component = EngineDownloads.Components[id];
            Assert.Equal("file", component.Kind);
            Assert.Matches("^[A-F0-9]{64}$", component.Sha256);
            Assert.True(component.Size > 0);
        });
    }

    [Fact]
    public async Task Qr_converter_writes_decoded_contents_to_a_text_file()
    {
        const string contents = "https://example.test/filee-qr";
        var root = Path.Combine(Path.GetTempPath(), "filee-qr-converter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var matrix = new QRCodeWriter().encode(contents, BarcodeFormat.QR_CODE, 256, 256,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 1 });
            using var bitmap = new SKBitmap(matrix.Width, matrix.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            for (var y = 0; y < matrix.Height; y++)
                for (var x = 0; x < matrix.Width; x++)
                    bitmap.SetPixel(x, y, matrix[x, y] ? SKColors.Black : SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var input = Path.Combine(root, "qr.png");
            var output = Path.Combine(root, "qr-result.txt");
            await File.WriteAllBytesAsync(input, png!.ToArray(), TestContext.Current.CancellationToken);
            using var localOcr = new LocalOcrService(root, NullLogger<LocalOcrService>.Instance);
            var converter = new QrCodeConverter(localOcr);

            var files = await converter.ConvertAsync(
                new ConversionStep(input, "png", "qrtext", new Preset { TargetFormat = "qrtext" },
                    new OneOutputAllocator(output), root),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal([output], files);
            Assert.Equal(contents, await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Installed_OPUS_MT_models_translate_in_both_directions_when_a_local_test_root_is_provided()
    {
        var modelRoot = Environment.GetEnvironmentVariable("FILEE_TRANSLATION_MODEL_TEST_ROOT");
        Assert.SkipUnless(EngineDownloads.RuntimeIdentifier == "win-x64" && !string.IsNullOrWhiteSpace(modelRoot) && Directory.Exists(modelRoot),
            "Set FILEE_TRANSLATION_MODEL_TEST_ROOT to an installed model package to run this offline model smoke test.");

        using var translation = new OfflineTranslationService(modelRoot!, NullLogger<OfflineTranslationService>.Instance);
        Assert.True(translation.IsInstalled);

        var english = await translation.TranslateAsync("你好，世界。", OfflineTranslationService.EnglishTargetFormat,
            cancellationToken: TestContext.Current.CancellationToken);
        var chinese = await translation.TranslateAsync("Hello, world.", OfflineTranslationService.ChineseTargetFormat,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual("你好，世界。", english);
        Assert.True(english.Any(char.IsAsciiLetter), $"Expected English text, got: {english}");
        Assert.True(english.Contains("hello", StringComparison.OrdinalIgnoreCase), $"Expected a translation of hello, got: {english}");
        Assert.NotEqual("Hello, world.", chinese);
        Assert.True(chinese.Any(character => character is >= '\u4e00' and <= '\u9fff'), $"Expected Chinese text, got: {chinese}");
        Assert.Contains("你好", chinese, StringComparison.Ordinal);
    }

    private sealed class OneOutputAllocator(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
