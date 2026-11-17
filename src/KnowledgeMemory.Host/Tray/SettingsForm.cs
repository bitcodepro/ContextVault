using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using KnowledgeMemory.Core;

namespace KnowledgeMemory.Host.Tray;

/// <summary>Port, start with Windows and auto-start by AI clients. Saved to appsettings.json; other settings are kept.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsForm : Form
{
    private readonly AppOptions _o;
    private readonly bool _serviceRunning;
    private readonly Func<bool, Task> _onSaved;
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
    private readonly Label _portState = new() { AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start Knowledge Memory when I sign in to Windows", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "Start the service automatically when an AI client needs it", AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(440, 0) };

    public SettingsForm(AppOptions o, bool serviceRunning, Func<bool, Task> onSaved)
    {
        _o = o;
        _serviceRunning = serviceRunning;
        _onSaved = onSaved;

        Text = "Knowledge Memory — Settings";
        Icon = TrayApp.CreateIcon(true);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont ?? Font;

        _port.Value = o.Server.Port;
        _startWithWindows.Checked = o.Server.StartWithWindows;
        _autoStart.Checked = o.Server.AutoStart;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var findFree = new Button { Text = "Find a free port", AutoSize = true };
        findFree.Click += (_, _) =>
        {
            try { _port.Value = PortCheck.FindFree(o.Server.Host, (int)_port.Value); }
            catch (KnowledgeException ex) { _error.Text = ex.Message; }
        };
        portRow.Controls.Add(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        portRow.Controls.Add(_port);
        portRow.Controls.Add(findFree);
        _port.ValueChanged += (_, _) => UpdatePortState();

        layout.Controls.Add(new Label { Text = "Web UI and AI clients use this local port (127.0.0.1).", AutoSize = true });
        layout.Controls.Add(portRow);
        layout.Controls.Add(_portState);
        layout.Controls.Add(new Label { Text = "AI clients pick up a new port on their next call; no change in their config is needed.", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) });
        layout.Controls.Add(Spacer());
        layout.Controls.Add(_startWithWindows);
        layout.Controls.Add(_autoStart);
        layout.Controls.Add(Spacer());
        layout.Controls.Add(new Label { Text = "Config file: " + (o.ConfigPath ?? "(none)"), AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) });
        layout.Controls.Add(new Label { Text = "Data folder: " + o.ResolvedDataDirectory, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) });
        layout.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += async (_, _) => await SaveAsync(save);
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(layout);
        UpdatePortState();
    }

    private static Control Spacer() => new Panel { Height = 8, Width = 1 };

    private bool PortIsUsable(int port) =>
        (port == _o.Server.Port && _serviceRunning) || PortCheck.IsFree(_o.Server.Host, port);

    private void UpdatePortState()
    {
        var port = (int)_port.Value;
        if (port == _o.Server.Port && _serviceRunning)
        {
            _portState.Text = "In use by Knowledge Memory now.";
            _portState.ForeColor = SystemColors.GrayText;
        }
        else if (PortCheck.IsFree(_o.Server.Host, port))
        {
            _portState.Text = "✓ Port is free.";
            _portState.ForeColor = Color.SeaGreen;
        }
        else
        {
            _portState.Text = "✗ Port is used by another program.";
            _portState.ForeColor = Color.Firebrick;
        }
    }

    private async Task SaveAsync(Button save)
    {
        _error.Text = "";
        var port = (int)_port.Value;
        if (!PortIsUsable(port))
        {
            _error.Text = $"Port {port} is used by another program. Choose another port or press \"Find a free port\".";
            return;
        }
        if (_o.ConfigPath is null)
        {
            _error.Text = "No config file: start the tray with --config <appsettings.json> to save settings.";
            return;
        }
        save.Enabled = false;
        try
        {
            ConfigWriter.Set(_o.ConfigPath,
                ("Server", "Port", JsonValue.Create(port)),
                ("Server", "StartWithWindows", JsonValue.Create(_startWithWindows.Checked)),
                ("Server", "AutoStart", JsonValue.Create(_autoStart.Checked)));
            var portChanged = port != _o.Server.Port;
            Close();
            await _onSaved(portChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KnowledgeException)
        {
            _error.Text = "Could not save: " + ex.Message;
            save.Enabled = true;
        }
    }
}
