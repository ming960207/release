using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook.Data;
using SkiaSharp;
using HookModifiers = Filee.Core.Settings.ModifierKeys;

namespace Filee.App.Tests;

public sealed class ScreenshotOcrShortcutTests
{
    [AvaloniaFact]
    public void ScreenshotWindowsLoadTheirXaml()
    {
        var localizer = new TestLocalizer();
        using var translation = new OfflineTranslationService(Path.GetTempPath(), NullLogger<OfflineTranslationService>.Instance);
        var result = new QuickOcrResultWindow(
            localizer,
            translation,
            NullLogger<QuickOcrResultWindow>.Instance,
            "OCR result",
            "recognized text",
            "recognized text",
            canTranslate: true);
        Assert.Equal("OCR result", result.Title);
        Assert.NotNull(result.Content);

        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var selection = new ScreenshotSelectionWindow(png.ToArray(), localizer);
        Assert.NotNull(selection.Content);
        selection.Show();
        selection.Close();
    }

    [Fact]
    public void DefaultShortcutMatchesOnlyAltQ()
    {
        var shortcut = new ScreenshotOcrShortcutSettings();

        Assert.True(TriggerService.IsScreenshotOcrShortcut(KeyCode.VcQ, HookModifiers.Alt, shortcut));
        Assert.False(TriggerService.IsScreenshotOcrShortcut(KeyCode.VcQ, HookModifiers.Ctrl | HookModifiers.Alt | HookModifiers.Shift, shortcut));
        Assert.False(TriggerService.IsScreenshotOcrShortcut(KeyCode.VcP, HookModifiers.Alt, shortcut));
    }

    [Fact]
    public void ShortcutMatcherUsesTheConfiguredSingleModifierAndKey()
    {
        var shortcut = new ScreenshotOcrShortcutSettings { Modifier = HookModifiers.Ctrl, Key = "P" };

        Assert.True(TriggerService.IsScreenshotOcrShortcut(KeyCode.VcP, HookModifiers.Ctrl, shortcut));
        Assert.False(TriggerService.IsScreenshotOcrShortcut(KeyCode.VcP, HookModifiers.Ctrl | HookModifiers.Shift, shortcut));
    }

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)]
    public void RecorderRejectsShortcutsWithoutExactlyOneModifier(KeyModifiers modifiers)
    {
        var store = new UserDataStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var saves = 0;
        using var viewModel = new ScreenshotOcrShortcutViewModel(store, new TestLocalizer(), null, null, () => saves++);
        viewModel.IsRecording = true;

        Assert.True(viewModel.Record(Key.P, modifiers));
        Assert.True(viewModel.IsRecording);
        Assert.NotNull(viewModel.Error);
        Assert.Equal(HookModifiers.Alt, store.Settings.ScreenshotOcrShortcut.Modifier);
        Assert.Equal("Q", store.Settings.ScreenshotOcrShortcut.Key);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void RecorderSavesOneModifierAndOneKey()
    {
        var store = new UserDataStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var saves = 0;
        using var viewModel = new ScreenshotOcrShortcutViewModel(store, new TestLocalizer(), null, null, () => saves++);
        viewModel.IsRecording = true;

        Assert.True(viewModel.Record(Key.P, KeyModifiers.Control));
        Assert.False(viewModel.IsRecording);
        Assert.Equal(HookModifiers.Ctrl, store.Settings.ScreenshotOcrShortcut.Modifier);
        Assert.Equal("P", store.Settings.ScreenshotOcrShortcut.Key);
        Assert.Equal("Ctrl + P", viewModel.ShortcutText);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void RecorderRejectsAKeyChordAlreadyAssignedToAFileeGesture()
    {
        var store = new UserDataStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        store.Settings.Triggers.Add(new TriggerGesture
        {
            Kind = TriggerKind.KeyChord,
            Modifiers = HookModifiers.Ctrl,
            Key = "P",
        });
        var saves = 0;
        using var viewModel = new ScreenshotOcrShortcutViewModel(store, new TestLocalizer(), null, null, () => saves++);
        viewModel.IsRecording = true;

        Assert.True(viewModel.Record(Key.P, KeyModifiers.Control));
        Assert.True(viewModel.IsRecording);
        Assert.NotNull(viewModel.Error);
        Assert.Equal(HookModifiers.Alt, store.Settings.ScreenshotOcrShortcut.Modifier);
        Assert.Equal(0, saves);
    }

    private sealed class TestLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => string.Format(key, args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
