using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VoiceAgent.Desktop;

internal sealed class UpdateWindow : Window
{
    private readonly TextBlock _title = TrayvoraTheme.Heading("Checking for updates…");
    private readonly TextBlock _detail = TrayvoraTheme.Hint("Contacting GitHub…");
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 5, IsIndeterminate = true };
    private readonly Button _update = new() { Content = "Update", IsEnabled = false, MinWidth = 96 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 88 };
    private readonly CancellationTokenSource _stop = new();

    public UpdateWindow(Func<CancellationToken, Task<AvailableUpdate?>> check,
        Func<AvailableUpdate, Action<string, double?>, CancellationToken, Task> install)
    {
        Title = "Trayvora · Software update";
        Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TrayvoraTheme.Apply(this);
        TrayvoraTheme.StyleControls(_update, _cancel);
        TrayvoraTheme.Primary(_update);
        var current = GitHubUpdate.CurrentVersion();
        Content = new Border { Padding = new Thickness(24), Child = new StackPanel
        {
            Spacing = 17, Children =
            {
                TrayvoraTheme.Caption(current is null ? "TRAYVORA" : $"TRAYVORA   /   {current}"),
                TrayvoraTheme.Card(_title, _detail, _bar),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { _cancel, _update } },
            },
        } };
        AvailableUpdate? release = null;
        _cancel.Click += (_, _) => Close();
        Closed += (_, _) => _stop.Cancel();
        Opened += async (_, _) =>
        {
            try
            {
                release = await check(_stop.Token);
                if (_stop.IsCancellationRequested) return;
                _bar.IsVisible = false;
                _title.Text = release is null ? "You’re up to date" : $"Trayvora {release.Version} is available";
                _detail.Text = release is null ? "This is the latest release on GitHub." : "Download the update and restart Trayvora. Your chats, settings and models stay here.";
                _update.IsVisible = _update.IsEnabled = release is not null;
                if (release is null) _cancel.Content = "Close";
            }
            catch (Exception error) { Failed(error); }
        };
        _update.Click += async (_, _) =>
        {
            if (release is null || !_update.IsEnabled) return;
            _update.IsEnabled = false;
            _bar.IsVisible = _bar.IsIndeterminate = true;
            _title.Text = $"Updating to {release.Version}";
            _detail.Text = "Downloading from GitHub…";
            try
            {
                await install(release, (phase, share) =>
                {
                    _detail.Text = phase;
                    _bar.IsIndeterminate = share is null;
                    if (share is { } value) _bar.Value = value;
                }, _stop.Token);
            }
            catch (Exception error) { Failed(error); }
        };
        void Failed(Exception error)
        {
            if (_stop.IsCancellationRequested) return;
            _title.Text = "Update unavailable";
            _detail.Text = error.Message;
            _bar.IsVisible = false;
            _update.IsEnabled = _update.IsVisible = release is not null;
            _cancel.Content = "Close";
            ErrorLog.Write(error, "Updating Trayvora");
        }
    }
}
