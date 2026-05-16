using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;                 // LINQ
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

#region Models

record User(int Id, string Name);
record Bookmark(int Id, int UserId, string Name, string Url);
record HistoryEntry(int Id, int UserId, string Url, DateTime VisitedAt);
record SettingsRow(int UserId, string Homepage);

#endregion

#region Data Access (SQLite)

static class Db
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniBrowserDb");
    private static readonly string PathDb = System.IO.Path.Combine(Dir, "browser.db");
    public static string ConnectionString => $"Data Source={PathDb};Cache=Shared";

    public static void Ensure()
    {
        Directory.CreateDirectory(Dir);
        var firstCreate = !File.Exists(PathDb);
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS Users(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS Bookmarks(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  UserId INTEGER NOT NULL,
  Name TEXT NOT NULL,
  Url TEXT NOT NULL,
  CONSTRAINT fk_bm_user FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS History(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  UserId INTEGER NOT NULL,
  Url TEXT NOT NULL,
  VisitedAt TEXT NOT NULL,
  CONSTRAINT fk_hist_user FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Settings(
  UserId INTEGER PRIMARY KEY,
  Homepage TEXT NOT NULL,
  CONSTRAINT fk_set_user FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
);
";
        cmd.ExecuteNonQuery();

        if (firstCreate)
        {
            // Seed default user + homepage
            var uid = InsertUser(con, "default");
            UpsertHomepage(con, uid, "https://hw.ac.uk");
        }
    }

    public static int InsertUser(SqliteConnection con, string name)
    {
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Users(Name) VALUES($n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        var id = Convert.ToInt32((long)cmd.ExecuteScalar()!);
        tx.Commit();
        return id;
    }

    public static int InsertUser(string name)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        return InsertUser(con, name);
    }

    public static List<User> GetUsers()
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM Users ORDER BY Name;";
        using var rd = cmd.ExecuteReader();
        var list = new List<User>();
        while (rd.Read())
            list.Add(new User(rd.GetInt32(0), rd.GetString(1)));
        return list;
    }

    public static void UpsertHomepage(int userId, string homepage)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        UpsertHomepage(con, userId, homepage);
    }

    private static void UpsertHomepage(SqliteConnection con, int userId, string homepage)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Settings(UserId, Homepage) VALUES($u, $h)
ON CONFLICT(UserId) DO UPDATE SET Homepage = excluded.Homepage;
";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$h", homepage);
        cmd.ExecuteNonQuery();
    }

    public static string GetHomepage(int userId)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Homepage FROM Settings WHERE UserId=$u;";
        cmd.Parameters.AddWithValue("$u", userId);
        var v = cmd.ExecuteScalar();
        return v is string s && !string.IsNullOrWhiteSpace(s) ? s : "https://hw.ac.uk";
    }

    public static List<Bookmark> GetBookmarks(int userId)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Id, UserId, Name, Url FROM Bookmarks WHERE UserId=$u ORDER BY Name;";
        cmd.Parameters.AddWithValue("$u", userId);
        using var rd = cmd.ExecuteReader();
        var list = new List<Bookmark>();
        while (rd.Read()) list.Add(new Bookmark(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3)));
        return list;
    }

    public static int AddBookmark(int userId, string name, string url)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Bookmarks(UserId, Name, Url) VALUES($u, $n, $l); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$l", url.Trim());
        return Convert.ToInt32((long)cmd.ExecuteScalar()!);
    }

    public static void UpdateBookmark(int id, string name, string url)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Bookmarks SET Name=$n, Url=$l WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$l", url.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void DeleteBookmark(int id)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Bookmarks WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void AddHistory(int userId, string url, DateTime when)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO History(UserId, Url, VisitedAt) VALUES($u,$l,$t);";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$l", url);
        cmd.Parameters.AddWithValue("$t", when.ToUniversalTime().ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public static List<HistoryEntry> GetHistory(int userId, int take = 500)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT Id, UserId, Url, VisitedAt FROM History WHERE UserId=$u ORDER BY datetime(VisitedAt) DESC LIMIT {take};";
        cmd.Parameters.AddWithValue("$u", userId);
        using var rd = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (rd.Read())
        {
            var dt = DateTime.Parse(rd.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);
            list.Add(new HistoryEntry(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), dt));
        }
        return list;
    }

    public static void ClearHistory(int userId)
    {
        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE UserId=$u;";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    }
}

#endregion

#region HTTP Status Probe (no EnsureSuccessStatusCode)

static class HttpStatusProbe
{
    private static readonly HttpClient _client = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = false
    });

    public static async Task<(int code, string reason)> ProbeAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MiniBrowser/1.0)");
            var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            // Preserve non-2xx codes; do not EnsureSuccessStatusCode
            return ((int)resp.StatusCode, resp.ReasonPhrase ?? "");
        }
        catch
        {
            // Fallback GET (some servers disallow HEAD)
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MiniBrowser/1.0)");
                var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return ((int)resp.StatusCode, resp.ReasonPhrase ?? "");
            }
            catch (Exception ex2)
            {
                return (0, ex2.Message);
            }
        }
    }
}

#endregion

#region Forms

class MainForm : Form
{
    // UI
    private readonly WebBrowser web = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
    private readonly TextBox address = new() { BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly ToolStripStatusLabel statusLabel = new("Ready");
    private readonly ToolStripStatusLabel httpLabel = new("HTTP: -");
    private readonly ComboBox userCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };

    // Lists
    private readonly ListView lvBookmarks = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly ListView lvHistory = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };

    // State
    private List<User> users = new();
    private int currentUserId;

    public MainForm()
    {
        Text = "Mini Browser — Menus + Shortcuts, Multi-User DB";
        Width = 1280; Height = 800; StartPosition = FormStartPosition.CenterScreen;

        // Menu
        var menu = new MenuStrip();
        var mFile = new ToolStripMenuItem("&File");
        var mNav  = new ToolStripMenuItem("&Navigate");
        var mBm   = new ToolStripMenuItem("&Bookmarks");
        var mView = new ToolStripMenuItem("&View");
        var mUser = new ToolStripMenuItem("&User");
        var mHelp = new ToolStripMenuItem("&Help");

        // Status
        var status = new StatusStrip();
        status.Items.Add(statusLabel);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(httpLabel);

        // Toolbar row: Address + Go + User combo
        var tool = new ToolStrip();
        var btnGo = new ToolStripButton("Go");
        var lblUser = new ToolStripLabel("User:");
        var hostUserCombo = new ToolStripControlHost(userCombo);
        tool.Items.Add(new ToolStripLabel("URL:"));
        var addressHost = new ToolStripControlHost(address) { AutoSize = false, Width = 800 };
        tool.Items.Add(addressHost);
        tool.Items.Add(btnGo);
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(lblUser);
        tool.Items.Add(hostUserCombo);

        // Split panels: left (tabs) / right (browser)
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 420 };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabBm = new TabPage("Bookmarks");
        var tabHist = new TabPage("History");
        tabBm.Controls.Add(lvBookmarks);
        tabHist.Controls.Add(lvHistory);
        tabs.TabPages.Add(tabBm);
        tabs.TabPages.Add(tabHist);

        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(web);

        // Columns
        lvBookmarks.Columns.Add("Name", 180);
        lvBookmarks.Columns.Add("URL", 220);
        lvHistory.Columns.Add("Visited", 160);
        lvHistory.Columns.Add("URL", 260);

        // Context menus
        var ctxBm = new ContextMenuStrip();
        var miBmOpen = ctxBm.Items.Add("Open");
        var miBmEdit = ctxBm.Items.Add("Edit…");
        var miBmDel  = ctxBm.Items.Add("Delete");
        lvBookmarks.ContextMenuStrip = ctxBm;

        var ctxHist = new ContextMenuStrip();
        var miHOpen = ctxHist.Items.Add("Open");
        var miHDel  = ctxHist.Items.Add("Delete");
        var miHClear = ctxHist.Items.Add("Clear All");
        lvHistory.ContextMenuStrip = ctxHist;

        // Menu items + shortcuts
        var miExit = new ToolStripMenuItem("E&xit", null, (_,__) => Close());
        miExit.ShortcutKeys = Keys.Alt | Keys.F4;
        mFile.DropDownItems.Add(miExit);

        var miBack = new ToolStripMenuItem("&Back", null, (_,__) => TryBack());
        var miFwd  = new ToolStripMenuItem("&Forward", null, (_,__) => TryForward());
        var miHome = new ToolStripMenuItem("&Home", null, (_,__) => GoHome());
        var miSetHome = new ToolStripMenuItem("&Set Homepage…", null, (_,__) => SetHome());
        miBack.ShortcutKeys = Keys.Alt | Keys.Left;
        miFwd.ShortcutKeys  = Keys.Alt | Keys.Right;
        miHome.ShortcutKeys = Keys.Control | Keys.Home;
        miSetHome.ShortcutKeys = Keys.Control | Keys.S;
        mNav.DropDownItems.AddRange(new[] { miBack, miFwd, new ToolStripSeparator(), miHome, miSetHome });

        var miAddBm = new ToolStripMenuItem("&Add Bookmark (Ctrl+D)", null, (_,__) => AddBookmark());
        miAddBm.ShortcutKeys = Keys.Control | Keys.D;
        var miEditBm = new ToolStripMenuItem("&Edit Selected…", null, (_,__) => EditBookmark());
        var miDelBm = new ToolStripMenuItem("&Delete Selected", null, (_,__) => DeleteBookmark());
        mBm.DropDownItems.AddRange(new[] { miAddBm, miEditBm, miDelBm });

        var miFocusUrl = new ToolStripMenuItem("&Focus URL (Ctrl+L)", null, (_,__) => { address.Focus(); address.SelectAll(); });
        miFocusUrl.ShortcutKeys = Keys.Control | Keys.L;
        var miToggleBm = new ToolStripMenuItem("Show &Bookmarks (Ctrl+Shift+B)", null, (_,__) => { tabs.SelectedTab = tabBm; split.Panel1Collapsed = false; });
        var miToggleHist = new ToolStripMenuItem("Show &History (Ctrl+H)", null, (_,__) => { tabs.SelectedTab = tabHist; split.Panel1Collapsed = false; });
        var miClearHist = new ToolStripMenuItem("&Clear History (Ctrl+Shift+H)", null, (_,__) => ClearHistory());
        miToggleBm.ShortcutKeys = Keys.Control | Keys.Shift | Keys.B;
        miToggleHist.ShortcutKeys = Keys.Control | Keys.H;
        miClearHist.ShortcutKeys = Keys.Control | Keys.Shift | Keys.H;
        mView.DropDownItems.AddRange(new[] { miFocusUrl, new ToolStripSeparator(), miToggleBm, miToggleHist, miClearHist });

        var miSwitchUser = new ToolStripMenuItem("&Switch/Add User (Ctrl+U)", null, (_,__) => SwitchUser());
        miSwitchUser.ShortcutKeys = Keys.Control | Keys.U;
        mUser.DropDownItems.Add(miSwitchUser);

        var miAbout = new ToolStripMenuItem("&About", null, (_,__) => MessageBox.Show("Mini Browser — coursework build\nMenus+Shortcuts, Multi-User, SQLite (LINQ)\n© You", "About", MessageBoxButtons.OK, MessageBoxIcon.Information));
        mHelp.DropDownItems.Add(miAbout);

        menu.Items.AddRange(new[] { mFile, mNav, mBm, mView, mUser, mHelp });

        // Form layout
        Controls.Add(split);
        Controls.Add(tool);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        // Events
        Load += async (_, __) => await InitializeAsync();
        btnGo.Click += (_, __) => Navigate(address.Text);
        address.KeyDown += (s,e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(address.Text); } };

        web.Navigated += async (_, e) =>
        {
            var u = web.Url?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(u)) return;
            address.Text = u;
            Db.AddHistory(currentUserId, u, DateTime.Now);
            await RefreshHistoryAsync();
            _ = UpdateHttpStatusAsync(u);
        };

        // Bookmarks interactions
        lvBookmarks.DoubleClick += (_, __) => OpenSelectedBookmark();
        miBmOpen.Click += (_, __) => OpenSelectedBookmark();
        miBmEdit.Click += (_, __) => EditBookmark();
        miBmDel.Click +=  (_, __) => DeleteBookmark();

        // History interactions
        lvHistory.DoubleClick += (_, __) => OpenSelectedHistory();
        miHOpen.Click += (_, __) => OpenSelectedHistory();
        miHDel.Click += (_, __) => DeleteSelectedHistory();
        miHClear.Click += (_, __) => ClearHistory();

        // User combo
        userCombo.SelectedIndexChanged += async (_, __) =>
        {
            var sel = userCombo.SelectedItem as User;
            if (sel == null) return;
            currentUserId = sel.Id;
            await ReloadUserScopedUiAsync();
            GoHome(); // navigate to user's home
        };
    }

    private async Task InitializeAsync()
    {
        Db.Ensure();
        users = Db.GetUsers();
        // Ensure at least default user exists
        if (users.Count == 0)
        {
            Db.InsertUser("default");
            users = Db.GetUsers();
        }
        currentUserId = users.First().Id;

        // Bind users
        userCombo.Items.Clear();
        foreach (var u in users) userCombo.Items.Add(u);
        userCombo.DisplayMember = nameof(User.Name);
        userCombo.SelectedIndex = 0;

        // Load homepage & navigate
        var home = Db.GetHomepage(currentUserId);
        address.Text = home;
        Navigate(home);

        await ReloadUserScopedUiAsync();
    }

    private async Task ReloadUserScopedUiAsync()
    {
        await RefreshBookmarksAsync();
        await RefreshHistoryAsync();
        statusLabel.Text = $"User: {users.First(u => u.Id == currentUserId).Name}";
    }

    private async Task RefreshBookmarksAsync()
    {
        var items = Db.GetBookmarks(currentUserId);
        // LINQ: order by Name asc; materialize to list
        var view = items.OrderBy(b => b.Name).ToList();
        lvBookmarks.BeginUpdate();
        lvBookmarks.Items.Clear();
        foreach (var b in view)
        {
            var it = new ListViewItem(b.Name) { Tag = b };
            it.SubItems.Add(b.Url);
            lvBookmarks.Items.Add(it);
        }
        lvBookmarks.EndUpdate();
        await Task.CompletedTask;
    }

    private async Task RefreshHistoryAsync()
    {
        var items = Db.GetHistory(currentUserId, 500);
        // LINQ: distinct by adjacent duplicates, project to display model
        var ordered = items
            .OrderByDescending(h => h.VisitedAt)
            .ToList();

        lvHistory.BeginUpdate();
        lvHistory.Items.Clear();
        foreach (var h in ordered)
        {
            var it = new ListViewItem(h.VisitedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) { Tag = h };
            it.SubItems.Add(h.Url);
            lvHistory.Items.Add(it);
        }
        lvHistory.EndUpdate();
        await Task.CompletedTask;
    }

    private void Navigate(string raw)
    {
        var url = NormalizeUrl(raw);
        if (url == null)
        {
            MessageBox.Show("Enter a valid URL (e.g., https://example.com)", "Invalid URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            web.Navigate(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not navigate:\n{ex.Message}", "Navigate",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TryBack()     { try { if (web.CanGoBack) web.GoBack(); } catch { } }
    private void TryForward()  { try { if (web.CanGoForward) web.GoForward(); } catch { } }

    private void GoHome()
    {
        var home = Db.GetHomepage(currentUserId);
        address.Text = home;
        Navigate(home);
    }

    private void SetHome()
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Set homepage URL:", "Set Homepage", Db.GetHomepage(currentUserId));
        if (string.IsNullOrWhiteSpace(input)) return;
        var url = NormalizeUrl(input);
        if (url == null)
        {
            MessageBox.Show("Invalid URL.", "Set Homepage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Db.UpsertHomepage(currentUserId, url);
        address.Text = url;
        statusLabel.Text = "Homepage saved.";
    }

    // Bookmarks
    private void AddBookmark()
    {
        var curUrl = address.Text.Trim();
        var url = NormalizeUrl(curUrl);
        if (url == null)
        {
            MessageBox.Show("Navigate to a valid page first.", "Add Bookmark",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = Microsoft.VisualBasic.Interaction.InputBox("Bookmark name:", "Add Bookmark", url);
        if (string.IsNullOrWhiteSpace(name)) return;

        // Prevent dup by URL
        var existing = Db.GetBookmarks(currentUserId).FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            Db.AddBookmark(currentUserId, name.Trim(), url);
        else
            Db.UpdateBookmark(existing.Id, name.Trim(), url);

        _ = RefreshBookmarksAsync();
        statusLabel.Text = "Bookmark saved.";
    }

    private void EditBookmark()
    {
        if (lvBookmarks.SelectedItems.Count == 0) { MessageBox.Show("Select a bookmark.", "Edit"); return; }
        var b = (Bookmark)lvBookmarks.SelectedItems[0].Tag!;
        var newName = Microsoft.VisualBasic.Interaction.InputBox("Bookmark name:", "Edit Bookmark", b.Name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        var newUrl = Microsoft.VisualBasic.Interaction.InputBox("Bookmark URL:", "Edit Bookmark", b.Url);
        var norm = NormalizeUrl(newUrl);
        if (norm == null) { MessageBox.Show("Invalid URL.", "Edit Bookmark"); return; }
        Db.UpdateBookmark(b.Id, newName.Trim(), norm);
        _ = RefreshBookmarksAsync();
    }

    private void DeleteBookmark()
    {
        if (lvBookmarks.SelectedItems.Count == 0) { MessageBox.Show("Select a bookmark.", "Delete"); return; }
        var b = (Bookmark)lvBookmarks.SelectedItems[0].Tag!;
        if (MessageBox.Show($"Delete bookmark:\n{b.Name}\n{b.Url}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            Db.DeleteBookmark(b.Id);
            _ = RefreshBookmarksAsync();
        }
    }

    private void OpenSelectedBookmark()
    {
        if (lvBookmarks.SelectedItems.Count == 0) return;
        var b = (Bookmark)lvBookmarks.SelectedItems[0].Tag!;
        Navigate(b.Url);
    }

    // History actions
    private void OpenSelectedHistory()
    {
        if (lvHistory.SelectedItems.Count == 0) return;
        var h = (HistoryEntry)lvHistory.SelectedItems[0].Tag!;
        Navigate(h.Url);
    }

    private void DeleteSelectedHistory()
    {
        if (lvHistory.SelectedItems.Count == 0) return;
        // Simple approach: clear and rebuild without the selected row
        var sel = (HistoryEntry)lvHistory.SelectedItems[0].Tag!;
        using var con = new SqliteConnection(Db.ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM History WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", sel.Id);
        cmd.ExecuteNonQuery();
        _ = RefreshHistoryAsync();
    }

    private void ClearHistory()
    {
        if (MessageBox.Show("Clear entire history for this user?", "Clear History",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            Db.ClearHistory(currentUserId);
            _ = RefreshHistoryAsync();
        }
    }

    // Users
    private void SwitchUser()
    {
        var resp = Microsoft.VisualBasic.Interaction.InputBox("Switch to user (existing name) or enter new name:", "Switch/Add User", users.First(u => u.Id == currentUserId).Name);
        if (string.IsNullOrWhiteSpace(resp)) return;
        var name = resp.Trim();

        var existing = users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            // Create user + default homepage
            var newId = Db.InsertUser(name);
            Db.UpsertHomepage(newId, "https://hw.ac.uk");
            users = Db.GetUsers();
            userCombo.Items.Clear();
            foreach (var u in users) userCombo.Items.Add(u);
            userCombo.DisplayMember = nameof(User.Name);
            userCombo.SelectedItem = users.First(u => u.Id == newId);
        }
        else
        {
            userCombo.SelectedItem = existing;
        }
    }

    private async Task UpdateHttpStatusAsync(string url)
    {
        httpLabel.Text = "HTTP: …";
        var (code, reason) = await HttpStatusProbe.ProbeAsync(url);
        httpLabel.Text = code == 0 ? $"HTTP: (transport) {reason}" : $"HTTP: {code} {reason}";
    }

    private static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? s : null;
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