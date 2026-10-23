using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.App.Services;

/// <summary>Translates a plain-text file with the installed local Chinese ↔ English model.</summary>
public sealed class OfflineTranslationConverter(OfflineTranslationService translation) : IConverter
{
    public const string ConverterId = "offline-translation";

    private static readonly IReadOnlyList<ConversionEdge> SupportedEdges =
        new[] { "txt", "ocrtext" }
            .Select(source => new[]
            {
                new ConversionEdge(source, OfflineTranslationService.EnglishTargetFormat, Cost: 20),
                new ConversionEdge(source, OfflineTranslationService.ChineseTargetFormat, Cost: 20),
            })
            .SelectMany(edges => edges)
            .ToArray();

    public string Id => ConverterId;
    public string DisplayName => "Offline translation";
    public IReadOnlyList<ConversionEdge> Edges => SupportedEdges;
    public int MaxParallelism => 1;

    public EngineStatus GetStatus() => translation.GetStatus();

    public async Task<IReadOnlyList<string>> ConvertAsync(
        ConversionStep step,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var outputFormat = FormatRegistry.Get(step.To);
        var output = step.Output.Allocate(outputFormat.PrimaryExtension);
        if (output is null)
            return [];

        var source = await File.ReadAllTextAsync(step.InputPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
            throw new ConversionRefusedException("error.translation.empty_text");

        var translated = await translation.TranslateAsync(source, step.To, progress, cancellationToken);
        await File.WriteAllTextAsync(output, translated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return [output];
    }
}
