using KnowledgeMemory.Core;

namespace KnowledgeMemory.Tests;

public sealed class SettingsFormProbeTests
{
    [Fact]
    public void Settings_window_can_be_created_and_shown()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var o = new AppOptions { ConfigPath = Path.Combine(Path.GetTempPath(), "km-probe.json"), ResolvedDataDirectory = Path.GetTempPath() };
                var type = typeof(KnowledgeMemory.Host.Tray.TrayApp).Assembly.GetType("KnowledgeMemory.Host.Tray.SettingsForm")!;
                Func<bool, Task> saved = _ => Task.CompletedTask;
                using var form = (System.Windows.Forms.Form)Activator.CreateInstance(type, o, true, saved)!;
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                form.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.True(error is null, error?.ToString());
    }
}
