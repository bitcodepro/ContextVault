using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using KnowledgeMemory.Core;

namespace KnowledgeMemory.Host.Tray;

/// <summary>
/// "tray": runs the service inside a notification-area app. Menu: open the UI in the browser, settings
/// (port, start with Windows), data folder, restart, exit. Exiting the tray stops the service it started.
/// If the service is already running for the same data folder (started elsewhere), the tray attaches to it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayApp
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static int Run(AppOptions o)
    {
        // One tray per data folder.
        var mutexName = "Local\\KnowledgeMemoryTray-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(o.ResolvedDataDirectory.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("Knowledge Memory is already in the tray for this data folder.");
            return 0;
        }
        FreeConsole(); // no console window while the tray runs

        var exit = 0;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new TrayContext(o);
            Application.Run(context);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    internal static Icon CreateIcon(bool running)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(running ? Color.FromArgb(37, 99, 235) : Color.FromArgb(120, 120, 120));
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("K", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}

[SupportedOSPlatform("windows")]
internal sealed class TrayContext : ApplicationContext
{
    private AppOptions _o;
    private ServerApp? _server;
    private bool _attached;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _restart;
    private readonly Icon _onIcon = TrayApp.CreateIcon(true);
    private readonly Icon _offIcon = TrayApp.CreateIcon(false);
    private SettingsForm? _settings;

    public TrayContext(AppOptions o)
    {
        _o = o;
        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _restart = new ToolStripMenuItem("Restart service", null, async (_, _) => await RestartAsync());
        var open = new ToolStripMenuItem("Open in browser", null, async (_, _) => await OpenBrowserAsync()) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        var copy = new ToolStripMenuItem("Copy MCP settings");
        foreach (var snippet in McpSnippets.All(Environment.ProcessPath!, _o.ConfigPath))
            copy.DropDownItems.Add(new ToolStripMenuItem(snippet.Menu, null, (_, _) => CopySnippet(snippet)));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            open,
            copy,
            new ToolStripSeparator(),
            _status,
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(_o.ResolvedDataDirectory)),
            _restart,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()),
        ]);
        _icon = new NotifyIcon { Icon = _offIcon, Text = "Knowledge Memory", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += async (_, e) => { if (e.Button == MouseButtons.Left) await OpenBrowserAsync(); };

        ApplyStartupSetting();
        _ = StartAsync();
    }

    private void ApplyStartupSetting()
    {
        try { new WindowsStartup().Apply(_o, Environment.ProcessPath!); }
        catch (Exception ex) { Notify("Could not change Windows startup: " + ex.Message, ToolTipIcon.Warning); }
    }

    private async Task StartAsync()
    {
        _attached = false;
        try
        {
            var server = ServerApp.Create(_o, logging: _ => { });
            try
            {
                await server.StartAsync();
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
            _server = server;
            SetStatus($"Running on port {_o.Server.Port}", running: true);
        }
        catch (KnowledgeException ex) when (ex.Code == ErrorCodes.Busy)
        {
            // Another process already serves this data folder: use it.
            _attached = true;
            SetStatus($"Running on port {_o.Server.Port} (started outside the tray)", running: true);
        }
        catch (Exception ex)
        {
            SetStatus("Not running: " + Short(ex.Message), running: false);
            Notify(ex.Message + "\nOpen Settings to change the port.", ToolTipIcon.Error);
        }
    }

    private async Task StopAsync()
    {
        if (_server is null) return;
        var s = _server;
        _server = null;
        await s.DisposeAsync();
    }

    private async Task RestartAsync()
    {
        if (_attached)
        {
            Notify("The service was started outside the tray (for example with 'serve'). Stop it there to restart from the tray.", ToolTipIcon.Info);
            return;
        }
        SetStatus("Restarting…", running: false);
        await StopAsync();
        try { _o = ConfigLoader.Load(_o.ConfigPath); }
        catch (KnowledgeException ex) { SetStatus("Not running: " + Short(ex.Message), false); Notify(ex.Message, ToolTipIcon.Error); return; }
        await StartAsync();
    }

    private async Task OpenBrowserAsync()
    {
        try
        {
            var url = await Cli.CreateLoginUrlAsync(_o);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Cannot open the web UI: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void CopySnippet(McpSnippets.Snippet snippet)
    {
        try
        {
            Clipboard.SetText(snippet.Text);
            Notify($"Copied: {snippet.Menu}.{Environment.NewLine}{snippet.WhereToPaste}", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            Notify("Could not copy to the clipboard: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            BringToFront(_settings);
            return;
        }
        _settings = new SettingsForm(_o, _server is not null || _attached, async changed =>
        {
            _o = ConfigLoader.Load(_o.ConfigPath);
            ApplyStartupSetting();
            if (changed) await RestartAsync();
        });
        BringToFront(_settings);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// When the tray was started by an AI client (hidden-window start flag), Windows applies that flag to the first
    /// window the program shows, so a plain Show() leaves it invisible. An explicit ShowWindow is not affected.
    /// </summary>
    private static void BringToFront(Form form)
    {
        form.Show();
        ShowWindow(form.Handle, 1 /* SW_SHOWNORMAL */);
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.TopMost = true;  // a tray app is not the foreground app: lift the window above others once
        form.TopMost = false;
        SetForegroundWindow(form.Handle);
        form.Activate();
    }

    private async Task ExitAsync()
    {
        _icon.Visible = false;
        await StopAsync();
        ExitThread();
    }

    private void SetStatus(string text, bool running)
    {
        _status.Text = text;
        _icon.Icon = running ? _onIcon : _offIcon;
        _icon.Text = Short("Knowledge Memory — " + text, 63);
        _restart.Enabled = !_attached;
    }

    private void Notify(string text, ToolTipIcon kind) => _icon.ShowBalloonTip(5000, "Knowledge Memory", text, kind);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string Short(string s, int max = 90) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _onIcon.Dispose();
            _offIcon.Dispose();
            _settings?.Dispose();
            StopAsync().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
