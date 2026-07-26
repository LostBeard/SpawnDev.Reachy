using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SpawnDev.Reachy.Rose;

/// <summary>
/// The tray-icon front end, so Aubs can use Rose without a terminal: a coloured dot
/// in the notification area (grey=off, blue=connecting, green=listening,
/// amber=thinking, cyan=talking), a right-click menu to start/stop her and pick a
/// character, and a "start with Windows" toggle.
/// </summary>
/// <remarks>
/// This hosts the exact same <see cref="RoseConversation"/> loop the console
/// <c>--talk</c> runs - it is a thin shell over it, not a second implementation, so
/// the voice/gestures/GPU path is identical. RoseConversation raises the state and
/// line events this reflects, and its dispose parks the robot, so the tray just wires
/// events to the icon and forwards menu clicks.
/// </remarks>
public sealed class RoseTray : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RoseCompanion";

    private readonly string _robotHost;
    private readonly NotifyIcon _icon;
    // A hidden control whose handle is created on the UI thread, used only to marshal
    // RoseConversation's background events (state/line) back onto the UI thread.
    private readonly Control _marshal = new();
    private readonly Dictionary<RoseState, Icon> _icons = new();

    private RoseConversation? _convo;
    private Character _character = CharacterLibrary.Default;
    private bool _busy;   // a start/stop is in flight - keep the menu from racing

    public RoseTray(string robotHost, bool autoStart = false)
    {
        _robotHost = robotHost;
        _ = _marshal.Handle;   // force handle creation now, on the UI thread

        foreach (RoseState s in Enum.GetValues<RoseState>())
            _icons[s] = MakeDotIcon(ColorFor(s));

        _icon = new NotifyIcon
        {
            Icon = _icons[RoseState.Off],
            Text = "Rose - off",
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _icon.ContextMenuStrip?.Show(Cursor.Position); };
        BuildMenu();

        // Win11 dumps new tray icons in the hidden overflow. Promote ours to always
        // visible so the status dot is actually glanceable. The registry entry is
        // created by Explorer when the icon first registers, which can lag the ctor,
        // so do it now and once more a moment later for the very first launch.
        PromoteTrayIcon();
        var promote = new System.Windows.Forms.Timer { Interval = 3000 };
        promote.Tick += (_, _) => { promote.Stop(); promote.Dispose(); PromoteTrayIcon(); };
        promote.Start();

        // Start Rose right away when asked (hands-free / autostart), once the message
        // loop is pumping so the UI marshaling is live.
        if (autoStart) Post(ToggleRose);
    }

    /// <summary>
    /// Marks our tray icon "promoted" (always shown, not in the Win11 overflow) so
    /// nobody has to hunt through Settings for the status dot. Matches the icon's
    /// registry entry by our own executable path, so it self-heals on any PC or after
    /// a rebuild to a new path.
    /// </summary>
    private void PromoteTrayIcon()
    {
        try
        {
            var exe = ExePath();
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name, writable: true);
                if (sub?.GetValue("ExecutablePath") is string p &&
                    string.Equals(p, exe, StringComparison.OrdinalIgnoreCase))
                    sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
        }
        catch { /* purely cosmetic - never fail startup over the tray-visibility flag */ }
    }

    // ---- menu ---------------------------------------------------------------

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("Rose - off") { Enabled = false, Name = "header" };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var startStop = new ToolStripMenuItem("Start Rose", null, (_, _) => ToggleRose()) { Name = "startstop" };
        menu.Items.Add(startStop);

        var chars = new ToolStripMenuItem("Character") { Name = "chars" };
        foreach (var c in CharacterLibrary.All)
        {
            var cap = c;
            var item = new ToolStripMenuItem(cap.Name, null, (_, _) => PickCharacter(cap))
            {
                Checked = cap.Name == _character.Name,
                Name = "char:" + cap.Name,
            };
            chars.DropDownItems.Add(item);
        }
        menu.Items.Add(chars);

        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = IsAutostartEnabled(),
            Name = "autostart",
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _icon.ContextMenuStrip = menu;
    }

    private void RefreshMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        var running = _convo is not null;
        ((ToolStripMenuItem)menu.Items["startstop"]!).Text = running ? "Stop Rose" : "Start Rose";
        ((ToolStripMenuItem)menu.Items["chars"]!).Enabled = running;
        foreach (ToolStripMenuItem item in ((ToolStripMenuItem)menu.Items["chars"]!).DropDownItems)
            item.Checked = item.Text == _character.Name;
        ((ToolStripMenuItem)menu.Items["autostart"]!).Checked = IsAutostartEnabled();
    }

    // ---- start / stop -------------------------------------------------------

    private async void ToggleRose()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_convo is null) await StartRoseAsync();
            else await StopRoseAsync();
        }
        finally { _busy = false; RefreshMenu(); }
    }

    private async Task StartRoseAsync()
    {
        ShowState(RoseState.Connecting);
        try
        {
            EnsureOllama();

            var convo = new RoseConversation(_robotHost, ModelDir(), cloneVoices: true, cloneSteps: 16);
            convo.StateChanged += s => Post(() => ShowState(s));
            _convo = convo;
            RefreshMenu();
            await convo.StartAsync();
        }
        catch (Exception ex)
        {
            _convo = null;
            ShowState(RoseState.Off);
            _icon.ShowBalloonTip(6000, "Rose could not start", Short(ex.Message), ToolTipIcon.Error);
        }
    }

    private async Task StopRoseAsync()
    {
        var convo = _convo;
        _convo = null;
        ShowState(RoseState.Off);
        if (convo is not null)
            try { await convo.DisposeAsync(); } catch { /* parking on the way out */ }
    }

    private async void PickCharacter(Character c)
    {
        _character = c;
        RefreshMenu();
        var convo = _convo;
        if (convo is not null)
            try { await convo.SwitchToAsync(c); } catch { }
    }

    // ---- status light -------------------------------------------------------

    private void ShowState(RoseState s)
    {
        _icon.Icon = _icons[s];
        var label = s switch
        {
            RoseState.Off => "off",
            RoseState.Connecting => "waking up...",
            RoseState.Listening => "listening",
            RoseState.Thinking => "thinking...",
            RoseState.Talking => "talking",
            _ => s.ToString().ToLowerInvariant(),
        };
        _icon.Text = $"Rose ({_character.Name}) - {label}";
        var header = _icon.ContextMenuStrip?.Items["header"] as ToolStripMenuItem;
        if (header is not null) header.Text = $"Rose ({_character.Name}) - {label}";
    }

    // ---- autostart (HKCU Run key) ------------------------------------------

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(RunValue) is not null) key.DeleteValue(RunValue, throwOnMissingValue: false);
        else key.SetValue(RunValue, $"\"{ExePath()}\" --tray {_robotHost}");
        RefreshMenu();
    }

    // ---- teardown -----------------------------------------------------------

    private async void ExitApp()
    {
        _icon.Visible = false;
        await StopRoseAsync();
        foreach (var ic in _icons.Values) ic.Dispose();
        _icon.Dispose();
        _marshal.Dispose();
        ExitThread();
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Runs an action on the UI thread; RoseConversation events arrive on background threads.</summary>
    private void Post(Action action)
    {
        if (_marshal.IsHandleCreated && !_marshal.IsDisposed)
            try { _marshal.BeginInvoke(action); } catch { /* shutting down */ }
    }

    private static string ModelDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "models");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "models");
    }

    private static string ExePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    /// <summary>
    /// Starts Ollama's server if it is not already answering. Rose's brain will not
    /// respond without it, and this is the exact gap that used to make her silent.
    /// </summary>
    private static void EnsureOllama()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try { if (http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode) return; }
        catch { /* not running - start it below */ }

        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        if (!File.Exists(exe)) return;   // let StartAsync surface the clearer "brain unreachable"

        Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true });
        // Give the server a moment to bind before StartAsync probes it.
        for (var i = 0; i < 20; i++)
        {
            try { if (http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode) return; }
            catch { }
            Thread.Sleep(500);
        }
    }

    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + "...";

    private static Color ColorFor(RoseState s) => s switch
    {
        RoseState.Off => Color.Gray,
        RoseState.Connecting => Color.DodgerBlue,
        RoseState.Listening => Color.LimeGreen,
        RoseState.Thinking => Color.Orange,
        RoseState.Talking => Color.DeepSkyBlue,
        _ => Color.Gray,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A small filled circle in the given colour, for the tray dot.</summary>
    private static Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(90, Color.Black), 2);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        var hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }   // clone owns its own handle
        finally { DestroyIcon(hicon); }
    }
}
