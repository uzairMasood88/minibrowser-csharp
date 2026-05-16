using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

internal class Settings
{
    public string Homepage { get; set; } = "https://hw.ac.uk";
}

internal static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiniBrowserCoursework");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath))
            {
                var defaults = new Settings(); // default homepage: https://hw.ac.uk
                Save(defaults);
                return defaults;
            }
            var json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return s ?? new Settings();
        }
        catch
        {
            // If settings are corrupt/inaccessible, continue with defaults
            return new Settings();
        }
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

public class BrowserForm : Form
{
    private readonly TextBox txtUrl = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly Button btnGo = new() { Text = "Go", Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button btnHome = new() { Text = "Home", Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button btnSetHome = new() { Text = "Set as Home", Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Button btnEditHome = new() { Text = "Edit Home…", Anchor = AnchorStyles.Top | AnchorStyles.Right };

    private readonly WebBrowser web = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
    private Settings settings;

    public BrowserForm()
    {
        Text = "Mini Browser — Homepage (Coursework)";
        Width = 1100;
        Height = 750;
        StartPosition = FormStartPosition.CenterScreen;

        // Load settings (default homepage is https://hw.ac.uk)
        settings = SettingsStore.Load();

        // Top bar layout
        var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
        Controls.Add(top);

        txtUrl.Text = settings.Homepage;
        txtUrl.Left = 8; txtUrl.Top = 8; txtUrl.Width = 640;
        btnGo.Top = btnHome.Top = btnSetHome.Top = btnEditHome.Top = 8;

        btnGo.Width = 56;
        btnHome.Width = 70;
        btnSetHome.Width = 110;
        btnEditHome.Width = 110;

        top.Controls.AddRange(new Control[] { txtUrl, btnGo, btnHome, btnSetHome, btnEditHome });
        top.Resize += (_, __) => LayoutTopBar(top);

        // Main browser area
        Controls.Add(web);

        // Events
        btnGo.Click += (_, __) => NavigateFromTextBox();
        btnHome.Click += (_, __) => Navigate(settings.Homepage);
        btnSetHome.Click += (_, __) => SetCurrentAsHome();
        btnEditHome.Click += (_, __) => EditHome();

        txtUrl.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromTextBox();
            }
        };

        web.Navigated += (_, __) =>
        {
            // reflect current URL
            if (web.Url is not null) txtUrl.Text = web.Url.AbsoluteUri;
        };

        // On startup: load homepage automatically
        Navigate(settings.Homepage);
    }

    private void LayoutTopBar(Panel top)
    {
        const int gap = 6;
        // Place right-aligned buttons: [Edit Home][Set as Home][Home][Go]
        btnEditHome.Left = top.ClientSize.Width - btnEditHome.Width - 8;
        btnSetHome.Left = btnEditHome.Left - btnSetHome.Width - gap;
        btnHome.Left = btnSetHome.Left - btnHome.Width - gap;
        btnGo.Left = btnHome.Left - btnGo.Width - gap;

        // URL textbox fills remaining space
        txtUrl.Width = btnGo.Left - txtUrl.Left - gap;
    }

    private void NavigateFromTextBox()
    {
        var raw = (txtUrl.Text ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return;

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }
        Navigate(raw);
    }

    private void Navigate(string url)
    {
        try
        {
            web.Navigate(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not navigate.\n\n{ex.Message}", "Navigate", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetCurrentAsHome()
    {
        var current = web.Url?.AbsoluteUri ?? txtUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            MessageBox.Show("No current page to set as homepage.", "Set as Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!current.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            current = "https://" + current;
        }

        settings.Homepage = current;
        SettingsStore.Save(settings);
        MessageBox.Show($"Homepage set to:\n{settings.Homepage}", "Set as Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void EditHome()
    {
        using var dlg = new EditHomeDialog(settings.Homepage);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var newUrl = dlg.Homepage.Trim();
            if (!newUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !newUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                newUrl = "https://" + newUrl;
            }
            settings.Homepage = newUrl;
            SettingsStore.Save(settings);
            txtUrl.Text = settings.Homepage;
            MessageBox.Show($"Homepage updated:\n{settings.Homepage}", "Edit Home", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

// Minimal input dialog for editing homepage
internal class EditHomeDialog : Form
{
    private readonly TextBox txt = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
    private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public string Homepage => txt.Text;

    public EditHomeDialog(string initial)
    {
        Text = "Edit Homepage";
        Width = 520;
        Height = 160;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        Controls.Add(panel);

        var lbl = new Label { Text = "Homepage URL:", AutoSize = true, Left = 8, Top = 12 };
        panel.Controls.Add(lbl);

        txt.Left = 8; txt.Top = lbl.Bottom + 6; txt.Width = ClientSize.Width - 40;
        txt.Text = string.IsNullOrWhiteSpace(initial) ? "https://hw.ac.uk" : initial;
        panel.Controls.Add(txt);

        var btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8)
        };
        btnPanel.Controls.Add(cancel);
        btnPanel.Controls.Add(ok);
        Controls.Add(btnPanel);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BrowserForm());
    }
}