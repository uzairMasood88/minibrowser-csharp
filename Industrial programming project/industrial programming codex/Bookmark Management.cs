using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

#region Models + Persistence

internal class Bookmark
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    [JsonIgnore] public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Url : $"{Name}  —  {Url}";
}

internal static class BookmarkStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiniBookmarksGui");
    private static readonly string FilePath = Path.Combine(Dir, "bookmarks.json");

    public static string StoragePath => FilePath;

    public static List<Bookmark> Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath))
            {
                // Seed with an empty list on first run
                Save(new List<Bookmark>());
                return new List<Bookmark>();
            }
            var json = File.ReadAllText(FilePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<Bookmark>>(json, opts) ?? new List<Bookmark>();
        }
        catch
        {
            // If corrupted, start fresh to avoid blocking UX
            return new List<Bookmark>();
        }
    }

    public static void Save(List<Bookmark> data)
    {
        Directory.CreateDirectory(Dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, opts));
    }
}

#endregion

#region Main Form

public class MainForm : Form
{
    // Left: bookmarks list + controls
    private readonly ListBox lst = new() { Dock = DockStyle.Fill };
    private readonly Button btnAdd = new() { Text = "Add", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnEdit = new() { Text = "Edit", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnDelete = new() { Text = "Delete", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnOpen = new() { Text = "Open (In App)", Dock = DockStyle.Top, Height = 32 };
    private readonly Button btnOpenExternal = new() { Text = "Open (External)", Dock = DockStyle.Top, Height = 32 };

    // Right: in-app browser + URL bar
    private readonly TextBox txtUrl = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly Button btnGo = new() { Text = "Go" };
    private readonly WebBrowser web = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };

    // Backing data
    private List<Bookmark> bookmarks = new();

    public MainForm()
    {
        Text = "Bookmarks Manager — Create / Edit / View / Delete (JSON)";
        Width = 1200;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        // Split layout (Left: list; Right: browser)
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 420
        };
        Controls.Add(split);

        // LEFT PANEL: List + buttons
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        split.Panel1.Controls.Add(left);

        var grpList = new GroupBox { Text = "Bookmarks (double-click to open)", Dock = DockStyle.Fill };
        grpList.Controls.Add(lst);

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0),
            WrapContents = true
        };
        panelButtons.Controls.AddRange(new Control[] { btnAdd, btnEdit, btnDelete, btnOpen, btnOpenExternal });

        left.Controls.Add(grpList);
        left.Controls.Add(panelButtons);
        left.Controls.SetChildIndex(panelButtons, 0);

        // RIGHT PANEL: URL bar + browser
        var right = new Panel { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(right);

        var urlBar = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
        txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        txtUrl.Left = 8; txtUrl.Top = 8; txtUrl.Width = 900;
        btnGo.Left = txtUrl.Right + 8; btnGo.Top = 6; btnGo.Width = 80; btnGo.Height = 28;

        urlBar.Controls.Add(txtUrl);
        urlBar.Controls.Add(btnGo);

        right.Controls.Add(web);
        right.Controls.Add(urlBar);

        // Events — list
        Load += (_, __) => LoadData();
        lst.DoubleClick += (_, __) => OpenSelectedInApp();
        lst.SelectedIndexChanged += (_, __) =>
        {
            if (lst.SelectedItem is Bookmark b)
                txtUrl.Text = b.Url;
        };

        // Events — buttons
        btnAdd.Click += (_, __) => AddBookmark();
        btnEdit.Click += (_, __) => EditBookmark();
        btnDelete.Click += (_, __) => DeleteBookmark();
        btnOpen.Click += (_, __) => OpenSelectedInApp();
        btnOpenExternal.Click += (_, __) => OpenSelectedExternal();

        // Events — URL bar
        btnGo.Click += (_, __) => Navigate(txtUrl.Text);
        txtUrl.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Navigate(txtUrl.Text);
            }
        };

        web.Navigated += (_, e) => txtUrl.Text = web.Url?.AbsoluteUri ?? txtUrl.Text;

        // Initial UI state
        txtUrl.Text = "https://example.com";
    }

    private void LoadData()
    {
        bookmarks = BookmarkStore.Load();
        BindList();
        // Auto-select first item (if any)
        if (lst.Items.Count > 0) lst.SelectedIndex = 0;
        // Show help
        if (bookmarks.Count == 0)
        {
            MessageBox.Show(
                "No bookmarks yet.\n\nUse 'Add' to create your first bookmark.\nDouble-click items to open them.",
                "Welcome",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private void BindList()
    {
        lst.BeginUpdate();
        lst.DataSource = null;
        lst.DisplayMember = null;
        lst.ValueMember = null;
        lst.DataSource = bookmarks;
        lst.DisplayMember = nameof(Bookmark.ToString);
        lst.EndUpdate();
    }

    private void AddBookmark()
    {
        using var dlg = new BookmarkDialog("Add Bookmark", "", "");
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var (name, url) = dlg.Values;
            var normalized = NormalizeUrl(url);
            if (normalized == null)
            {
                MessageBox.Show("Please enter a valid URL (e.g., https://example.com).", "Invalid URL",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Prevent duplicates by URL
            bookmarks.RemoveAll(b => string.Equals(b.Url, normalized, StringComparison.OrdinalIgnoreCase));
            bookmarks.Add(new Bookmark { Name = string.IsNullOrWhiteSpace(name) ? normalized : name.Trim(), Url = normalized });

            BookmarkStore.Save(bookmarks);
            BindList();
            SelectByUrl(normalized);
        }
    }

    private void EditBookmark()
    {
        if (lst.SelectedItem is not Bookmark sel)
        {
            MessageBox.Show("Select a bookmark to edit.", "Edit Bookmark",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new BookmarkDialog("Edit Bookmark", sel.Name, sel.Url);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var (name, url) = dlg.Values;
            var normalized = NormalizeUrl(url);
            if (normalized == null)
            {
                MessageBox.Show("Please enter a valid URL (e.g., https://example.com).", "Invalid URL",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // De-duplicate by URL (remove others with same URL)
            bookmarks.RemoveAll(b => !ReferenceEquals(b, sel) &&
                                     string.Equals(b.Url, normalized, StringComparison.OrdinalIgnoreCase));

            sel.Name = string.IsNullOrWhiteSpace(name) ? normalized : name.Trim();
            sel.Url = normalized;

            BookmarkStore.Save(bookmarks);
            BindList();
            SelectByUrl(normalized);
        }
    }

    private void DeleteBookmark()
    {
        if (lst.SelectedItem is not Bookmark sel)
        {
            MessageBox.Show("Select a bookmark to delete.", "Delete Bookmark",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show($"Delete this bookmark?\n\n{sel}",
            "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm == DialogResult.Yes)
        {
            bookmarks.Remove(sel);
            BookmarkStore.Save(bookmarks);
            BindList();
        }
    }

    private void OpenSelectedInApp()
    {
        if (lst.SelectedItem is not Bookmark sel)
        {
            MessageBox.Show("Select a bookmark to open.", "Open Bookmark",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Navigate(sel.Url);
    }

    private void OpenSelectedExternal()
    {
        if (lst.SelectedItem is not Bookmark sel)
        {
            MessageBox.Show("Select a bookmark to open.", "Open External",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenExternal(sel.Url);
    }

    private void Navigate(string? raw)
    {
        var normalized = NormalizeUrl(raw);
        if (normalized == null)
        {
            MessageBox.Show("Please enter a valid URL (e.g., https://example.com).", "Invalid URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            web.Navigate(normalized);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not navigate.\n\n{ex.Message}", "Navigate",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenExternal(string url)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open in external browser.\n\n{ex.Message}",
                "Open External", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectByUrl(string url)
    {
        for (int i = 0; i < lst.Items.Count; i++)
        {
            if (lst.Items[i] is Bookmark b &&
                string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                lst.SelectedIndex = i;
                break;
            }
        }
    }

    private static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = "https://" + s;
        }
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? s : null;
    }
}

#endregion

#region Dialog

internal class BookmarkDialog : Form
{
    private readonly TextBox txtName = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox txtUrl = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly Button btnOk = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
    private readonly Button btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public (string Name, string Url) Values => (txtName.Text, txtUrl.Text);

    public BookmarkDialog(string title, string name, string url)
    {
        Text = title;
        Width = 520;
        Height = 220;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var lblName = new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        var lblUrl = new Label { Text = "URL:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };

        txtName.Text = name;
        txtUrl.Text = url;

        txtName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        panel.Controls.Add(lblName, 0, 0);
        panel.Controls.Add(txtName, 1, 0);
        panel.Controls.Add(lblUrl, 0, 1);
        panel.Controls.Add(txtUrl, 1, 1);

        var btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8)
        };
        btnPanel.Controls.Add(btnCancel);
        btnPanel.Controls.Add(btnOk);

        Controls.Add(panel);
        Controls.Add(btnPanel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
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