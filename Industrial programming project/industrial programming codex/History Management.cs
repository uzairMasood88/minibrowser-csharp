using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

#region Persistence Models

internal class HistoryEntry
{
    public string Url { get; set; } = "";
    public DateTime VisitedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public override string ToString() => Url;
}

internal class HistorySnapshot
{
    public List<HistoryEntry> Entries { get; set; } = new();
    public int CurrentIndex { get; set; } = -1;
}

internal static class HistoryStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiniHistoryGui");
    private static readonly string FilePath = Path.Combine(Dir, "history.json");

    public static string StoragePath => FilePath;

    public static HistorySnapshot Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath))
            {
                var seed = new HistorySnapshot();
                Save(seed);
                return seed;
            }
            var json = File.ReadAllText(FilePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<HistorySnapshot>(json, opts) ?? new HistorySnapshot();
        }
        catch
        {
            // If corrupt/unreadable, start clean
            return new HistorySnapshot();
        }
    }

    public static void Save(HistorySnapshot snap)
    {
        Directory.CreateDirectory(Dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(snap, opts));
    }
}

#endregion

#region History Manager (logic only)

internal class HistoryManager
{
    public List<HistoryEntry> Entries { get; private set; } = new();
    public int CurrentIndex { get; private set; } = -1;

    public event Action? Changed; // signal UI to refresh

    public void Load(HistorySnapshot snap)
    {
        Entries = snap.Entries ?? new List<HistoryEntry>();
        CurrentIndex = Math.Min(Math.Max(snap.CurrentIndex, -1), Entries.Count - 1);
        Changed?.Invoke();
    }

    public HistorySnapshot Snapshot() => new HistorySnapshot
    {
        Entries = Entries,
        CurrentIndex = CurrentIndex
    };

    public bool CanGoBack => CurrentIndex > 0;
    public bool CanGoForward => CurrentIndex >= 0 && CurrentIndex < Entries.Count - 1;

    public string? CurrentUrl => (CurrentIndex >= 0 && CurrentIndex < Entries.Count) ? Entries[CurrentIndex].Url : null;

    // Add navigation with browser semantics:
    // - If we're not at the end (user went back), truncate forward then add
    // - Collapse consecutive duplicates
    public void Add(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = NormalizeUrl(url) ?? url;

        var last = CurrentUrl;
        if (string.Equals(last, url, StringComparison.OrdinalIgnoreCase))
        {
            // Same as current; no-op
            return;
        }

        // If not at tail, truncate forward
        if (CanGoForward)
        {
            Entries.RemoveRange(CurrentIndex + 1, Entries.Count - (CurrentIndex + 1));
        }

        // Collapse duplicate if last equals url (after truncation we check tail)
        if (Entries.Count > 0 && string.Equals(Entries[^1].Url, url, StringComparison.OrdinalIgnoreCase))
        {
            CurrentIndex = Entries.Count - 1;
        }
        else
        {
            Entries.Add(new HistoryEntry { Url = url, VisitedAt = DateTime.UtcNow });
            CurrentIndex = Entries.Count - 1;
        }

        Changed?.Invoke();
    }

    public string? Back()
    {
        if (!CanGoBack) return null;
        CurrentIndex--;
        Changed?.Invoke();
        return Entries[CurrentIndex].Url;
    }

    public string? Forward()
    {
        if (!CanGoForward) return null;
        CurrentIndex++;
        Changed?.Invoke();
        return Entries[CurrentIndex].Url;
    }

    public string? JumpTo(int index)
    {
        if (index < 0 || index >= Entries.Count) return null;
        CurrentIndex = index;
        Changed?.Invoke();
        return Entries[index].Url;
    }

    public void DeleteAt(int index)
    {
        if (index < 0 || index >= Entries.Count) return;
        Entries.RemoveAt(index);
        if (Entries.Count == 0)
        {
            CurrentIndex = -1;
        }
        else if (index <= CurrentIndex)
        {
            CurrentIndex = Math.Max(0, CurrentIndex - 1);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        Entries.Clear();
        CurrentIndex = -1;
        Changed?.Invoke();
    }

    private static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? s : null;
    }
}

#endregion

#region Main Form (GUI)

public class MainForm : Form
{
    // Left: History list + controls
    private readonly ListBox lst = new() { Dock = DockStyle.Fill };
    private readonly Button btnBack = new() { Text = "◀ Back", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnForward = new() { Text = "Forward ▶", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnOpenSel = new() { Text = "Open Selected", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnDeleteSel = new() { Text = "Delete Selected", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnClear = new() { Text = "Clear All", Dock = DockStyle.Top, Height = 32 };

    // Right: URL bar + browser
    private readonly TextBox txtUrl = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly Button btnGo = new() { Text = "Go" };
    private readonly Button btnOpenExternal = new() { Text = "Open External" };
    private readonly WebBrowser web = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };

    private readonly HistoryManager history = new();

    public MainForm()
    {
        Text = "History Manager — Persist + Click-to-Jump";
        Width = 1200;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 460
        };
        Controls.Add(split);

        // LEFT: history + buttons
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        split.Panel1.Controls.Add(left);

        var grpList = new GroupBox { Text = "History (double-click to jump)", Dock = DockStyle.Fill };
        grpList.Controls.Add(lst);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 170,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        btns.Controls.AddRange(new Control[] { btnBack, btnForward, btnOpenSel, btnDeleteSel, btnClear });

        left.Controls.Add(grpList);
        left.Controls.Add(btns);
        left.Controls.SetChildIndex(btns, 0);

        // RIGHT: url bar + browser
        var right = new Panel { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(right);

        var urlBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        txtUrl.Left = 8; txtUrl.Top = 8; txtUrl.Width = 740;
        btnGo.Left = txtUrl.Right + 8; btnGo.Top = 6; btnGo.Width = 80; btnGo.Height = 28;
        btnOpenExternal.Left = btnGo.Right + 8; btnOpenExternal.Top = 6; btnOpenExternal.Width = 120; btnOpenExternal.Height = 28;

        urlBar.Controls.Add(txtUrl);
        urlBar.Controls.Add(btnGo);
        urlBar.Controls.Add(btnOpenExternal);

        right.Controls.Add(web);
        right.Controls.Add(urlBar);

        // Wire events
        Load += OnLoad;
        FormClosing += OnClosing;

        lst.DoubleClick += (_, __) => JumpToSelected();
        lst.SelectedIndexChanged += (_, __) => UpdateButtons();

        btnBack.Click += (_, __) => NavigateBack();
        btnForward.Click += (_, __) => NavigateForward();
        btnOpenSel.Click += (_, __) => JumpToSelected();
        btnDeleteSel.Click += (_, __) => DeleteSelected();
        btnClear.Click += (_, __) => ClearAll();

        btnGo.Click += (_, __) => NavigateFromBar();
        btnOpenExternal.Click += (_, __) => OpenExternal(txtUrl.Text);

        txtUrl.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromBar();
            }
        };

        web.Navigated += (_, e) =>
        {
            // WebBrowser fires many navigations; we record top-level URL
            var u = web.Url?.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(u))
            {
                history.Add(u);
                txtUrl.Text = u;
                BindHistoryList(selectCurrent: true);
                SaveHistory();
            }
        };

        history.Changed += () =>
        {
            BindHistoryList(selectCurrent: true);
            UpdateButtons();
        };
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        // Restore history
        var snap = HistoryStore.Load();
        history.Load(snap);

        // If no history, seed with hw.ac.uk
        if (history.CurrentUrl == null)
        {
            var seed = "https://hw.ac.uk";
            txtUrl.Text = seed;
            Navigate(seed); // this will add+persist via Navigated event
        }
        else
        {
            txtUrl.Text = history.CurrentUrl!;
            Navigate(history.CurrentUrl!); // opens last session current page
        }

        // Initial paint
        BindHistoryList(selectCurrent: true);
        UpdateButtons();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        SaveHistory();
    }

    private void SaveHistory()
    {
        try { HistoryStore.Save(history.Snapshot()); }
        catch { /* ignore */ }
    }

    private void NavigateFromBar()
    {
        Navigate(txtUrl.Text);
    }

    private void Navigate(string? raw)
    {
        var url = NormalizeUrl(raw);
        if (url == null)
        {
            MessageBox.Show("Enter a valid absolute URL (e.g., https://example.com).", "Invalid URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            web.Navigate(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not navigate.\n\n{ex.Message}", "Navigate",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void NavigateBack()
    {
        var url = history.Back();
        if (url != null) Navigate(url);
    }

    private void NavigateForward()
    {
        var url = history.Forward();
        if (url != null) Navigate(url);
    }

    private void JumpToSelected()
    {
        var i = lst.SelectedIndex;
        var url = history.JumpTo(i);
        if (url != null) Navigate(url);
    }

    private void DeleteSelected()
    {
        var i = lst.SelectedIndex;
        if (i < 0) return;
        history.DeleteAt(i);
        SaveHistory();

        // Keep URL bar in sync
        txtUrl.Text = history.CurrentUrl ?? txtUrl.Text;
        if (history.CurrentUrl != null) Navigate(history.CurrentUrl);
    }

    private void ClearAll()
    {
        var confirm = MessageBox.Show("Clear entire history?", "Clear History",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm == DialogResult.Yes)
        {
            history.Clear();
            SaveHistory();
            txtUrl.Text = "";
        }
    }

    private void BindHistoryList(bool selectCurrent)
    {
        lst.BeginUpdate();
        lst.DataSource = null;
        var view = new List<string>();
        for (int i = 0; i < history.Entries.Count; i++)
        {
            var marker = (i == history.CurrentIndex) ? "• " : "  ";
            var t = history.Entries[i].VisitedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            view.Add($"{marker}{history.Entries[i].Url}   [{t}]");
        }
        lst.DataSource = view;
        lst.EndUpdate();

        if (selectCurrent && history.CurrentIndex >= 0 && history.CurrentIndex < lst.Items.Count)
        {
            lst.SelectedIndex = history.CurrentIndex;
        }
    }

    private void UpdateButtons()
    {
        btnBack.Enabled = history.CanGoBack;
        btnForward.Enabled = history.CanGoForward;
        btnOpenSel.Enabled = lst.SelectedIndex >= 0;
        btnDeleteSel.Enabled = lst.SelectedIndex >= 0;
        btnClear.Enabled = history.Entries.Count > 0;
    }

    private static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? s : null;
    }

    private static void OpenExternal(string? url)
    {
        var normalized = NormalizeUrl(url);
        if (normalized == null) return;
        try
        {
            var psi = new ProcessStartInfo { FileName = normalized, UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open external browser.\n\n{ex.Message}",
                "Open External", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

#endregion

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}