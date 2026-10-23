using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.App.Services;

/// <summary>Recognizes Chinese and Latin text in images using Filee's bundled PP-OCRv5 model.</summary>
public sealed class OcrConverter(LocalOcrService ocr) : IConverter
{
    private static readonly IReadOnlyList<ConversionEdge> SupportedEdges =
        new[] { "png", "jpg", "webp", "bmp", "gif", "ico", "wbmp" }
            .Select(format => new ConversionEdge(format, "ocrtext", Cost: 20, ImageSourcesOnly: true))
            .ToArray();

    public string Id => "local-ocr";
    public string DisplayName => "OCR";
    public IReadOnlyList<ConversionEdge> Edges => SupportedEdges;
    public int MaxParallelism => 1;

    public EngineStatus GetStatus() => EngineStatus.Available(
        "Bundled local OCR model", "PP-OCRv5");

    public async Task<IReadOnlyList<string>> ConvertAsync(
        ConversionStep step,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];

        var image = await File.ReadAllBytesAsync(step.InputPath, cancellationToken);
        progress?.Report(0.1);
        var text = await ocr.RecognizeTextAsync(image, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            throw new ConversionRefusedException("error.recognition.no_text");

        await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
