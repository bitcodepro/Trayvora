using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace VoiceAgent.Desktop;

internal sealed class WorkspaceWindow : Window
{
    public WorkspaceWindow(string? initial, Action<string> select)
    {
        Title = "Local workspace";
        Width = 530;
        Height = 380;
        var path = new TextBlock { Text = "Choose a project folder on E:.", TextWrapping = TextWrapping.Wrap };
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120 };
        var choose = new Button { Content = "Choose folder…" };
        var open = new Button { Content = "Open in File Explorer", IsEnabled = false };
        var git = new Button { Content = "Read Git branch and HEAD", IsEnabled = false };
        var create = new Button { Content = "Initialize Git in this empty folder", IsEnabled = initial is not null };
        string? selected = initial;
        if (initial is not null)
        {
            path.Text = initial;
            open.IsEnabled = git.IsEnabled = create.IsEnabled = true;
        }
        var closed = false;
        Closed += (_, _) => closed = true;

        choose.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a local project on E:",
                });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } directory || closed) return;
                selected = LocalActions.Workspace(directory);
                select(selected);
                path.Text = selected;
                open.IsEnabled = git.IsEnabled = create.IsEnabled = true;
                detail.Text = "";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                detail.Text = error.Message;
            }
        };

        open.Click += (_, _) =>
        {
            if (selected is null) return;
            try { LocalActions.OpenWorkspace(selected); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                detail.Text = "Could not open the selected folder.";
            }
        };

        git.Click += async (_, _) =>
        {
            if (selected is not { } directory) return;
            choose.IsEnabled = git.IsEnabled = false;
            try
            {
                detail.Text = await LocalActions.ReadGitHeadAsync(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                detail.Text = "Could not read Git metadata. Check the selected repository and VOICEAGENT_GIT_EXE.";
            }
            finally
            {
                if (!closed) choose.IsEnabled = git.IsEnabled = true;
            }
        };

        create.Click += async (_, _) =>
        {
            if (selected is not { } directory) return;
            create.IsEnabled = choose.IsEnabled = git.IsEnabled = false;
            try
            {
                await LocalActions.InitializeEmptyRepositoryAsync(directory);
                detail.Text = "Git repository created in the selected empty folder.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                detail.Text = error is IOException ? error.Message : "Git could not initialize the selected folder.";
            }
            finally
            {
                if (!closed) create.IsEnabled = choose.IsEnabled = git.IsEnabled = true;
            }
        };

        Title = "Trayvora · Local workspace";
        Width = 560;
        Height = 470;
        TrayvoraTheme.Apply(this);
        TrayvoraTheme.Primary(choose);
        TrayvoraTheme.StyleControls(open, git, create, detail);
        path.FontWeight = FontWeight.SemiBold;
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Local workspace", FontSize = 20, FontWeight = FontWeight.SemiBold },
                TrayvoraTheme.Card(TrayvoraTheme.Caption("Selected folder"), path, TrayvoraTheme.Wrap(choose, open)),
                TrayvoraTheme.Card(TrayvoraTheme.Caption("Git"), TrayvoraTheme.Wrap(git, create), detail),
                TrayvoraTheme.Hint("Local actions run only when selected here."),
            },
        };
    }
}
