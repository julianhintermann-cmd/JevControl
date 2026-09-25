using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Kairo.Core.Agent;
using Forms = System.Windows.Forms;

namespace Kairo.App.Services;

/// <summary>
/// System tray icon with a Fluent styled menu: open overlay, recent tasks, pause computer control,
/// settings, exit. Left click opens the overlay.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _normalIcon;
    private readonly System.Drawing.Icon _pausedIcon;
    private readonly Window _menuHost;
    private readonly Func<IReadOnlyList<AgentTask>> _recentTasks;
    private bool _paused;

    public TrayIconService(Func<IReadOnlyList<AgentTask>> recentTasks)
    {
        _recentTasks = recentTasks;
        _normalIcon = LoadIcon("kairo.ico");
        _pausedIcon = LoadIcon("kairo-paused.ico");
        _icon = new Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = "Kairo – Strg + Alt + K",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) { OpenOverlayRequested?.Invoke(this, EventArgs.Empty); }
            else if (e.Button == Forms.MouseButtons.Right) { ShowMenu(); }
        };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);

        // Invisible host window: WPF context menus only close correctly when their owner is the foreground window.
        _menuHost = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            Left = -10000,
            Top = -10000,
        };
    }

    public event EventHandler? OpenOverlayRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? PauseToggled;
    public event EventHandler<AgentTask>? TaskSelected;
    public event EventHandler? BalloonClicked;

    public string HotkeyText { get; set; } = "Strg + Alt + K";

    private static System.Drawing.Icon LoadIcon(string name)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Kairo;component/Assets/{name}"));
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _icon.Icon = paused ? _pausedIcon : _normalIcon;
        _icon.Text = paused ? "Kairo – Computersteuerung pausiert" : $"Kairo – {HotkeyText}";
    }

    public void SetBusy(string? status)
    {
        var text = status is null ? (_paused ? "Kairo – pausiert" : $"Kairo – {HotkeyText}") : $"Kairo – {status}";
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    public void ShowNotification(string title, string text, bool error = false)
    {
        _icon.ShowBalloonTip(4000, title, text.Length > 250 ? text[..249] + "…" : text, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = _menuHost };

        var open = new MenuItem { Header = "Kairo öffnen", InputGestureText = HotkeyText };
        open.Click += (_, _) => OpenOverlayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);

        var recent = new MenuItem { Header = "Letzte Aufgaben" };
        var tasks = _recentTasks();
        if (tasks.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = "Noch keine Aufgaben", IsEnabled = false });
        }
        foreach (var task in tasks.Take(8))
        {
            var mark = task.State switch
            {
                AgentTaskState.Completed => "✓",
                AgentTaskState.Cancelled => "■",
                _ => "!",
            };
            var header = $"{mark}  {Shorten(task.Instruction, 48)}";
            var item = new MenuItem { Header = header, ToolTip = $"{task.State.ToGerman()} · {task.CreatedAt:HH:mm} · {task.ResultMessage ?? task.ErrorMessage}" };
            item.Click += (_, _) => TaskSelected?.Invoke(this, task);
            recent.Items.Add(item);
        }
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());

        var pause = new MenuItem { Header = "Computersteuerung pausieren", IsCheckable = true, IsChecked = _paused };
        pause.Click += (_, _) => PauseToggled?.Invoke(this, pause.IsChecked);
        menu.Items.Add(pause);

        var settings = new MenuItem { Header = "Einstellungen" };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Beenden" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);

        menu.Closed += (_, _) => _menuHost.Hide();
        _menuHost.Show();
        _menuHost.Activate();
        Kairo.Windows.Windows.WindowService.ForceForeground(new WindowInteropHelper(_menuHost).Handle);
        menu.IsOpen = true;
    }

    private static string Shorten(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normalIcon.Dispose();
        _pausedIcon.Dispose();
        _menuHost.Close();
    }
}
