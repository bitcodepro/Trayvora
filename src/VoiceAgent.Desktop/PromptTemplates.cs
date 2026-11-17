using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceAgent.Desktop;

/// <summary>Saved messages you send often ("review the diff and discuss with Claude"), stored in settings\templates.json.</summary>
internal sealed class PromptTemplates(string file)
{
    public const int MaxCount = 40;

    public IReadOnlyList<string> Load()
    {
        try { return SettingsFile.Read<List<string>>(file) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Adds <paramref name="text"/> at the top (once). Returns false if it could not be saved.</summary>
    public bool Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        var items = Load().Where(item => item != text).Prepend(text).Take(MaxCount).ToList();
        return Save(items);
    }

    public bool Remove(string text) => Save(Load().Where(item => item != text).ToList());

    private bool Save(List<string> items)
    {
        try { SettingsFile.Write(file, items); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A button whose menu inserts a template, saves the current text, or deletes a template.</summary>
    public Button Button(Func<string?> current, Action<string> insert)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15 },
        };
        button.Classes.Add("round");
        TrayvoraTheme.StyleControls(button);
        ToolTip.SetTip(button, "Templates: insert a saved message or save this one");
        Avalonia.Automation.AutomationProperties.SetName(button, "Templates");
        var menu = new MenuFlyout();
        button.Flyout = menu;
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var text = current()?.Trim() ?? "";
            var save = new MenuItem { Header = "Save current text as template", IsEnabled = text.Length > 0 };
            save.Click += (_, _) => Add(text);
            menu.Items.Add(save);
            var items = Load();
            if (items.Count > 0) menu.Items.Add(new Separator());
            foreach (var item in items)
            {
                var label = item.ReplaceLineEndings(" ");
                var entry = new MenuItem { Header = label.Length <= 70 ? label : label[..70] + "…" };
                ToolTip.SetTip(entry, item);
                entry.Click += (_, _) => insert(item);
                var remove = new MenuItem { Header = "Delete this template" };
                remove.Click += (_, _) => Remove(item);
                var insertItem = new MenuItem { Header = "Insert" };
                insertItem.Click += (_, _) => insert(item);
                entry.Items.Add(insertItem);
                entry.Items.Add(remove);
                menu.Items.Add(entry);
            }
            if (items.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No templates yet. Type a message, then save it here.", IsEnabled = false });
        };
        return button;
    }

    /// <summary>Inserts <paramref name="template"/> into an editor, after any text already there.</summary>
    public static void InsertInto(TextBox editor, string template)
    {
        var current = editor.Text ?? "";
        editor.Text = string.IsNullOrWhiteSpace(current) ? template : current.TrimEnd() + "\n" + template;
        editor.CaretIndex = editor.Text.Length;
        editor.Focus();
    }
}
