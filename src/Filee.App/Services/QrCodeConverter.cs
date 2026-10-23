using System.Text;
using Filee.Core.Conversion;

namespace Filee.App.Services;

/// <summary>Extracts QR code contents from an image into a plain-text file using the bundled ZXing decoder.</summary>
public sealed class QrCodeConverter(LocalOcrService ocr) : IConverter
{
    private static readonly IReadOnlyList<ConversionEdge> SupportedEdges =
        new[] { "png", "jpg", "webp", "bmp", "gif", "ico", "wbmp" }
            .Select(format => new ConversionEdge(format, "qrtext", Cost: 20, ImageSourcesOnly: true))
            .ToArray();

    public string Id => "qr-decoder";
    public string DisplayName => "QR code decoder";
    public IReadOnlyList<ConversionEdge> Edges => SupportedEdges;
    public int MaxParallelism => 1;

    public EngineStatus GetStatus() => EngineStatus.Available("Built in; QR decoding needs no model.", "ZXing.Net 0.16.11");

    public async Task<IReadOnlyList<string>> ConvertAsync(
        ConversionStep step,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var output = step.Output.Allocate(Filee.Core.Formats.FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];

        var image = await File.ReadAllBytesAsync(step.InputPath, cancellationToken);
        progress?.Report(0.1);
        var qrCodes = await ocr.DecodeQrCodesAsync(image, cancellationToken);
        if (qrCodes.Count == 0)
            throw new ConversionRefusedException("error.recognition.no_qr");

        var text = string.Join(Environment.NewLine, qrCodes.Select(qr => qr.Text));
        await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
