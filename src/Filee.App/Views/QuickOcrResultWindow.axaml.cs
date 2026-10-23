using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Filee.App.Views;

public partial class QuickOcrResultWindow : Window
{
    private readonly ILocalizer _loc;
    private readonly OfflineTranslationService _translation;
    private readonly ILogger<QuickOcrResultWindow> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string? _copyText;
    private string? _translatedText;

    public QuickOcrResultWindow(
        ILocalizer loc,
        OfflineTranslationService translation,
        ILogger<QuickOcrResultWindow> logger,
        string title,
        string text,
        string? copyText,
        bool canTranslate)
    {
        _loc = loc;
        _translation = translation;
        _logger = logger;
        _copyText = string.IsNullOrWhiteSpace(copyText) ? null : copyText;
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ResultText.Text = text;
        TranslationHint.IsVisible = canTranslate;
        TranslateButton.IsVisible = canTranslate;
        CopyButton.IsVisible = _copyText is not null;
        StatusText.Text = _copyText is not null ? loc["ocr.copy_pending"] : "";
        Closed += (_, _) => _shutdown.Cancel();
        Opened += (_, _) =>
        {
            if (_copyText is not null)
                _ = CopyResultAsync();
        };
    }

    private async void OnCopy(object? sender, RoutedEventArgs e) => await CopyResultAsync();

    private async void OnTranslate(object? sender, RoutedEventArgs e)
    {
        var source = ResultText.Text ?? "";
        var start = Math.Clamp(Math.Min(ResultText.SelectionStart, ResultText.SelectionEnd), 0, source.Length);
        var end = Math.Clamp(Math.Max(ResultText.SelectionStart, ResultText.SelectionEnd), 0, source.Length);
        var selected = end > start ? source[start..end] : "";
        var input = string.IsNullOrWhiteSpace(selected) ? source : selected;
        if (string.IsNullOrWhiteSpace(input))
            return;
        if (!_translation.IsInstalled)
        {
            StatusText.Text = _loc["ocr.translation_model_required"];
            return;
        }

        TranslateButton.IsEnabled = false;
        StatusText.Text = _loc["ocr.translation_in_progress"];
        try
        {
            var target = ContainsChinese(input)
                ? OfflineTranslationService.EnglishTargetFormat
                : OfflineTranslationService.ChineseTargetFormat;
            var translated = await Task.Run(
                () => _translation.TranslateAsync(input, target, cancellationToken: _shutdown.Token),
                _shutdown.Token);
            if (_shutdown.IsCancellationRequested)
                return;

            var firstTranslation = !TranslationPanel.IsVisible;
            _translatedText = translated;
            TranslationText.Text = translated;
            TranslationPanel.IsVisible = true;
            CopyTranslationButton.IsVisible = true;
            if (firstTranslation)
                Height = Math.Min(520, Height + 140);
            StatusText.Text = _loc["ocr.translation_done"];
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Closing the result window cancels an in-flight translation.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Offline translation from the OCR result window failed");
            StatusText.Text = _loc["ocr.translation_failed"];
        }
        finally
        {
            if (!_shutdown.IsCancellationRequested)
                TranslateButton.IsEnabled = true;
        }
    }

    private async void OnCopyTranslation(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_translatedText))
            return;
        try
        {
            if (Clipboard is null)
                throw new InvalidOperationException("Clipboard is unavailable.");
            await Clipboard.SetTextAsync(_translatedText);
            StatusText.Text = _loc["ocr.translation_copied"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not copy the OCR translation to the clipboard");
            StatusText.Text = _loc["ocr.copy_failed"];
        }
    }

    private async Task CopyResultAsync()
    {
        if (_copyText is null)
            return;
        try
        {
            if (Clipboard is null)
                throw new InvalidOperationException("Clipboard is unavailable.");
            await Clipboard.SetTextAsync(_copyText);
            StatusText.Text = _loc["ocr.copied"];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText.Text = _loc["ocr.copy_failed"];
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static bool ContainsChinese(string text) => text.Any(character =>
        character is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF');
}
