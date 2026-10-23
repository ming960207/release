using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Filee.App.Services;

/// <summary>Runs the optional, local OPUS-MT Chinese ↔ English ONNX models.</summary>
public sealed class OfflineTranslationService : IDisposable
{
    public const string EnglishTargetFormat = "txt-en";
    public const string ChineseTargetFormat = "txt-zh";
    private const long DecoderStartToken = 65000;
    private const long EndOfSentenceToken = 0;
    private const int MaximumSourceTokens = 480;
    private const int MaximumOutputTokens = 256;
    private const int MaximumChunkCharacters = 280;
    private const string MarkerName = ".filee-component";

    private static readonly ModelFiles ChineseToEnglish = new(
        "zh-en",
        "mt-zh-en-encoder", "encoder_model_q4f16.onnx",
        "mt-zh-en-decoder", "decoder_model_q4f16.onnx",
        "mt-zh-en-source-tokenizer", "source.spm",
        "mt-zh-en-target-tokenizer", "target.spm",
        "mt-zh-en-vocab", "vocab.json");

    private static readonly ModelFiles EnglishToChinese = new(
        "en-zh",
        "mt-en-zh-encoder", "encoder_model_q4f16.onnx",
        "mt-en-zh-decoder", "decoder_model_q4f16.onnx",
        "mt-en-zh-source-tokenizer", "source.spm",
        "mt-en-zh-target-tokenizer", "target.spm",
        "mt-en-zh-vocab", "vocab.json");

    private readonly string _modelRoot;
    private readonly ILogger<OfflineTranslationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarianSession? _session;
    private string? _loadedDirection;

    public OfflineTranslationService(string modelRoot, ILogger<OfflineTranslationService> logger)
    {
        _modelRoot = modelRoot;
        _logger = logger;
    }

    public bool IsInstalled => EngineDownloads.RuntimeIdentifier == "win-x64"
                              && AllFilesInstalled(ChineseToEnglish)
                              && AllFilesInstalled(EnglishToChinese);

    public EngineStatus GetStatus()
    {
        if (EngineDownloads.RuntimeIdentifier != "win-x64")
            return EngineStatus.Unavailable("engine.reason.windows_only");
        return IsInstalled
            ? EngineStatus.Available(detail: "OPUS-MT q4f16 · runs offline", version: "OPUS-MT q4f16")
            : EngineStatus.Unavailable("engine.reason.translation_model_not_installed");
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetFormat,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        if (!IsInstalled)
            throw new ConversionRefusedException("engine.reason.translation_model_not_installed");

        var files = targetFormat switch
        {
            EnglishTargetFormat => ChineseToEnglish,
            ChineseTargetFormat => EnglishToChinese,
            _ => throw new ArgumentOutOfRangeException(nameof(targetFormat), targetFormat, "Unknown translation direction."),
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = GetSession(files);
            var lines = text.Split('\n');
            var result = new StringBuilder(text.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = lines[i];
                var lineEnding = "";
                if (i < lines.Length - 1)
                {
                    lineEnding = line.EndsWith('\r') ? "\r\n" : "\n";
                    if (lineEnding == "\r\n")
                        line = line[..^1];
                }
                result.Append(TranslateLine(session, line, cancellationToken));
                result.Append(lineEnding);
                progress?.Report((i + 1) / (double)lines.Length);
            }
            return result.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Unload();
        _gate.Dispose();
    }

    public void Unload()
    {
        _gate.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
            _loadedDirection = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private MarianSession GetSession(ModelFiles files)
    {
        if (_loadedDirection == files.Direction && _session is not null)
            return _session;

        _session?.Dispose();
        _session = null;
        _loadedDirection = null;
        var model = new MarianSession(
            ComponentPath(files.EncoderId, files.EncoderFile),
            ComponentPath(files.DecoderId, files.DecoderFile),
            ComponentPath(files.SourceTokenizerId, files.SourceTokenizerFile),
            ComponentPath(files.TargetTokenizerId, files.TargetTokenizerFile),
            ComponentPath(files.VocabularyId, files.VocabularyFile),
            _logger);
        _session = model;
        _loadedDirection = files.Direction;
        return model;
    }

    private string TranslateLine(MarianSession session, string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        var leadingLength = line.TakeWhile(char.IsWhiteSpace).Count();
        var trailingLength = line.Reverse().TakeWhile(char.IsWhiteSpace).Count();
        var bodyLength = line.Length - leadingLength - trailingLength;
        if (bodyLength <= 0)
            return line;

        var leading = line[..leadingLength];
        var body = line.Substring(leadingLength, bodyLength);
        var trailing = line[(leadingLength + bodyLength)..];
        var output = new StringBuilder(line.Length);
        output.Append(leading);
        foreach (var chunk in SplitIntoChunks(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append(session.Translate(chunk, cancellationToken));
        }
        output.Append(trailing);
        return output.ToString();
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + MaximumChunkCharacters);
            if (end < text.Length)
            {
                for (var candidate = end; candidate > start + MaximumChunkCharacters / 2; candidate--)
                {
                    if (char.IsWhiteSpace(text[candidate - 1]) || "。！？；，.!?;,".Contains(text[candidate - 1]))
                    {
                        end = candidate;
                        break;
                    }
                }
            }
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
                end--;
            yield return text[start..end];
            start = end;
        }
    }

    private bool AllFilesInstalled(ModelFiles files) =>
        HasComponent(files.EncoderId, files.EncoderFile)
        && HasComponent(files.DecoderId, files.DecoderFile)
        && HasComponent(files.SourceTokenizerId, files.SourceTokenizerFile)
        && HasComponent(files.TargetTokenizerId, files.TargetTokenizerFile)
        && HasComponent(files.VocabularyId, files.VocabularyFile);

    private bool HasComponent(string componentId, string fileName)
    {
        if (!EngineDownloads.Components.TryGetValue(componentId, out var component))
            return false;
        var folder = Path.Combine(_modelRoot, componentId);
        var asset = Path.Combine(folder, fileName);
        var marker = Path.Combine(folder, MarkerName);
        try
        {
            return File.Exists(asset) && File.Exists(marker)
                   && string.Equals(File.ReadAllText(marker).Trim(), component.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ComponentPath(string componentId, string fileName) => Path.Combine(_modelRoot, componentId, fileName);

    private sealed record ModelFiles(
        string Direction,
        string EncoderId, string EncoderFile,
        string DecoderId, string DecoderFile,
        string SourceTokenizerId, string SourceTokenizerFile,
        string TargetTokenizerId, string TargetTokenizerFile,
        string VocabularyId, string VocabularyFile);

    private sealed class MarianSession : IDisposable
    {
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly SentencePieceTokenizer _sourceTokenizer;
        private readonly SentencePieceTokenizer _targetTokenizer;
        private readonly string _encoderInputIds;
        private readonly string? _encoderAttentionMask;
        private readonly string _encoderHiddenOutput;
        private readonly string _decoderInputIds;
        private readonly string _decoderEncoderHidden;
        private readonly string? _decoderAttentionMask;
        private readonly string _decoderLogits;
        private readonly IReadOnlyDictionary<string, int> _modelVocabulary;
        private readonly IReadOnlyDictionary<int, string> _modelTokens;
        private readonly ILogger _logger;

        public MarianSession(
            string encoderPath,
            string decoderPath,
            string sourceTokenizerPath,
            string targetTokenizerPath,
            string vocabularyPath,
            ILogger logger)
        {
            _logger = logger;
            using var encoderOptions = CreateSessionOptions();
            using var decoderOptions = CreateSessionOptions();
            var encoder = new InferenceSession(encoderPath, encoderOptions);
            InferenceSession? decoder = null;
            SentencePieceTokenizer? sourceTokenizer = null;
            SentencePieceTokenizer? targetTokenizer = null;
            string encoderInputIds;
            string? encoderAttentionMask;
            string encoderHiddenOutput;
            string decoderInputIds;
            string decoderEncoderHidden;
            string? decoderAttentionMask;
            string decoderLogits;
            try
            {
                decoder = new InferenceSession(decoderPath, decoderOptions);
                using var sourceModel = File.OpenRead(sourceTokenizerPath);
                using var targetModel = File.OpenRead(targetTokenizerPath);
                sourceTokenizer = SentencePieceTokenizer.Create(sourceModel, false, false);
                targetTokenizer = SentencePieceTokenizer.Create(targetModel, false, false);
                using var vocabularyStream = File.OpenRead(vocabularyPath);
                var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabularyStream)
                                 ?? throw new InvalidDataException("The translation vocabulary is invalid.");
                var tokens = vocabulary.ToDictionary(pair => pair.Value, pair => pair.Key);

                encoderInputIds = InputName(encoder, "input_ids");
                encoderAttentionMask = OptionalInputName(encoder, "attention_mask");
                encoderHiddenOutput = OutputName(encoder, "last_hidden_state");
                decoderInputIds = InputName(decoder!, "input_ids", "decoder_input_ids");
                decoderEncoderHidden = InputName(decoder!, "encoder_hidden_states");
                decoderAttentionMask = OptionalInputName(decoder!, "encoder_attention_mask", "attention_mask");
                decoderLogits = OutputName(decoder!, "logits");
                _modelVocabulary = vocabulary;
                _modelTokens = tokens;
            }
            catch
            {
                encoder.Dispose();
                decoder?.Dispose();
                throw;
            }

            _encoder = encoder;
            _decoder = decoder!;
            _sourceTokenizer = sourceTokenizer!;
            _targetTokenizer = targetTokenizer!;
            _encoderInputIds = encoderInputIds;
            _encoderAttentionMask = encoderAttentionMask;
            _encoderHiddenOutput = encoderHiddenOutput;
            _decoderInputIds = decoderInputIds;
            _decoderEncoderHidden = decoderEncoderHidden;
            _decoderAttentionMask = decoderAttentionMask;
            _decoderLogits = decoderLogits;
            _logger.LogInformation("Loaded OPUS-MT q4f16 translation models from {EncoderPath}", encoderPath);
        }

        public string Translate(string text, CancellationToken cancellationToken)
        {
            var sourceIds = new List<long>();
            foreach (var token in _sourceTokenizer.EncodeToTokens(text, out _, false, false, true, true))
            {
                if (_modelVocabulary.TryGetValue(token.Value, out var modelId))
                    sourceIds.Add(modelId);
                else if (_modelVocabulary.TryGetValue("<unk>", out var unknownId))
                    sourceIds.Add(unknownId);
                else
                    throw new InvalidDataException("The translation vocabulary is missing the unknown token.");
            }
            if (sourceIds.Count == 0)
                return "";
            if (sourceIds.Count > MaximumSourceTokens)
                throw new InvalidDataException($"The translation chunk exceeds {MaximumSourceTokens} source tokens.");
            sourceIds.Add(EndOfSentenceToken);

            var sourceTensor = new DenseTensor<long>(sourceIds.ToArray(), [1, sourceIds.Count]);
            var sourceMask = new DenseTensor<long>(Enumerable.Repeat(1L, sourceIds.Count).ToArray(), [1, sourceIds.Count]);
            var encoderInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_encoderInputIds, sourceTensor) };
            if (_encoderAttentionMask is { } encoderMaskName)
                encoderInputs.Add(NamedOnnxValue.CreateFromTensor(encoderMaskName, sourceMask));

            cancellationToken.ThrowIfCancellationRequested();
            using var encoderResults = _encoder.Run(encoderInputs);
            var encodedOutput = encoderResults.First(output => output.Name == _encoderHiddenOutput).AsTensor<float>();
            var encoderHidden = new DenseTensor<float>(encodedOutput.ToArray(), encodedOutput.Dimensions.ToArray());

            var decoderIds = new List<long> { DecoderStartToken };
            var resultIds = new List<long>(MaximumOutputTokens);
            for (var tokenIndex = 0; tokenIndex < MaximumOutputTokens; tokenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoderTensor = new DenseTensor<long>(decoderIds.ToArray(), [1, decoderIds.Count]);
                var decoderInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_decoderInputIds, decoderTensor),
                    NamedOnnxValue.CreateFromTensor(_decoderEncoderHidden, encoderHidden),
                };
                if (_decoderAttentionMask is { } decoderMaskName)
                    decoderInputs.Add(NamedOnnxValue.CreateFromTensor(decoderMaskName, sourceMask));

                using var decoderResults = _decoder.Run(decoderInputs);
                var logits = decoderResults.First(output => output.Name == _decoderLogits).AsTensor<float>();
                var nextToken = ArgMaxLastToken(logits);
                if (nextToken == EndOfSentenceToken)
                    break;
                if (nextToken != DecoderStartToken)
                    resultIds.Add(checked((int)nextToken));
                decoderIds.Add(nextToken);
            }

            return DecodeOutput(resultIds);
        }

        public void Dispose()
        {
            _encoder.Dispose();
            _decoder.Dispose();
        }

        private string DecodeOutput(IEnumerable<long> modelIds)
        {
            var tokenizerIds = new List<int>();
            foreach (var modelId in modelIds)
            {
                if (modelId == EndOfSentenceToken || modelId == DecoderStartToken)
                    continue;
                if (!_modelTokens.TryGetValue(checked((int)modelId), out var token))
                    continue;
                if (_targetTokenizer.Vocabulary.TryGetValue(token, out var tokenizerId))
                    tokenizerIds.Add(tokenizerId);
                else if (_targetTokenizer.Vocabulary.TryGetValue("<unk>", out var unknownId))
                    tokenizerIds.Add(unknownId);
            }
            return _targetTokenizer.Decode(tokenizerIds);
        }

        private static SessionOptions CreateSessionOptions() => new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4),
            InterOpNumThreads = 1,
        };

        private static string InputName(InferenceSession session, params string[] candidates) =>
            candidates.FirstOrDefault(candidate => session.InputMetadata.Keys.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The translation model is missing input '{string.Join(" or ", candidates)}'.");

        private static string? OptionalInputName(InferenceSession session, params string[] candidates) =>
            candidates.FirstOrDefault(candidate => session.InputMetadata.Keys.Contains(candidate, StringComparer.OrdinalIgnoreCase));

        private static string OutputName(InferenceSession session, string candidate) =>
            session.OutputMetadata.Keys.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            ?? session.OutputMetadata.Keys.First();

        private static long ArgMaxLastToken(Tensor<float> logits)
        {
            var dimensions = logits.Dimensions.ToArray();
            if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[2] <= 0)
                throw new InvalidDataException("The translation decoder returned an unsupported logits shape.");
            var sequenceLength = dimensions[1];
            var vocabularySize = dimensions[2];
            var values = logits.ToArray();
            var offset = (sequenceLength - 1) * vocabularySize;
            var bestIndex = 0;
            var bestValue = float.NegativeInfinity;
            for (var index = 0; index < vocabularySize; index++)
            {
                if (index == DecoderStartToken)
                    continue;
                var value = values[offset + index];
                if (value <= bestValue)
                    continue;
                bestIndex = index;
                bestValue = value;
            }
            return bestIndex;
        }
    }
}
