using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace VoiceAgent.Desktop;

internal sealed record UiLanguageOptions(string Language = "en");

/// <summary>
/// Interface language. English texts in the code are the keys; Assets\uk.json maps them to Ukrainian.
/// When Ukrainian is chosen, every text, button, header, placeholder, tooltip and window title that
/// exactly matches a key is replaced as it is set, so pages built later and status messages are covered too.
/// Text typed by the user and replies from agents are never changed.
/// </summary>
internal static class UiLanguage
{
    public static readonly (string Code, string Name)[] Choices = [("en", "English"), ("uk", "Українська")];
    private static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();
    private static bool _hooked;

    public static string Current { get; private set; } = "en";

    public static string File(string settingsDirectory) => Path.Combine(settingsDirectory, "ui-language.json");

    public static string Saved(string settingsDirectory)
    {
        try { return SettingsFile.Read<UiLanguageOptions>(File(settingsDirectory))?.Language ?? "en"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "en"; }
    }

    public static void Save(string settingsDirectory, string code) => SettingsFile.Write(File(settingsDirectory), new UiLanguageOptions(code));

    /// <summary>Loads the saved language and starts translating. Call before any window is created.</summary>
    public static void Start(string settingsDirectory)
    {
        Current = Saved(settingsDirectory);
        if (Current == "en") return;
        _map = LoadMap(Current);
        if (_map.Count == 0 || _hooked) return;
        _hooked = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (block is SelectableTextBlock || block.Text is not { } text) return;
            if (Lookup(text) is { } translated) block.Text = translated;
        });
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control, _) =>
        {
            if (control.Content is string text && Lookup(text) is { } translated) control.Content = translated;
        });
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<HeaderedContentControl>((control, _) =>
        {
            if (control.Header is string text && Lookup(text) is { } translated) control.Header = translated;
        });
        HeaderedSelectingItemsControl.HeaderProperty.Changed.AddClassHandler<HeaderedSelectingItemsControl>((control, _) =>
        {
            if (control.Header is string text && Lookup(text) is { } translated) control.Header = translated;
        });
        TextBox.PlaceholderTextProperty.Changed.AddClassHandler<TextBox>((box, _) =>
        {
            if (box.PlaceholderText is { } text && Lookup(text) is { } translated) box.PlaceholderText = translated;
        });
        ComboBox.PlaceholderTextProperty.Changed.AddClassHandler<ComboBox>((box, _) =>
        {
            if (box.PlaceholderText is { } text && Lookup(text) is { } translated) box.PlaceholderText = translated;
        });
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (ToolTip.GetTip(control) is string text && Lookup(text) is { } translated) ToolTip.SetTip(control, translated);
        });
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, _) =>
        {
            if (window.Title is { } text && Lookup(text) is { } translated) window.Title = translated;
        });
        NativeMenuItem.HeaderProperty.Changed.AddClassHandler<NativeMenuItem>((item, _) =>
        {
            if (item.Header is { } text && Lookup(text) is { } translated) item.Header = translated;
        });
    }

    /// <summary>The translation of <paramref name="text"/> in the current language (the text itself if none).</summary>
    public static string T(string text) => Lookup(text) ?? text;

    internal static string? Lookup(string text) => _map.Count > 0 && _map.TryGetValue(text, out var translated) && translated != text ? translated : null;

    internal static IReadOnlyDictionary<string, string> LoadMap(string code)
    {
        using var stream = typeof(UiLanguage).Assembly.GetManifestResourceStream($"VoiceAgent.Desktop.Assets.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []; }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }
}
