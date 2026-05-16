using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using HtmlAgilityPack;

namespace MiniWinFormsBrowser
{
    public class MainForm : Form
    {
        // --- Controls ---
        private readonly TextBox txtAddress = new TextBox();
        private readonly Button btnGo = new Button();
        private readonly Button btnBack = new Button();
        private readonly Button btnForward = new Button();
        private readonly Button btnReload = new Button();

        private readonly Label lblTitle = new Label();
        private readonly ListBox lstTopLinks = new ListBox();
        private readonly TextBox txtRawHtml = new TextBox();

        private readonly StatusStrip statusStrip = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel httpStatusLabel = new ToolStripStatusLabel();

        // --- Networking & State ---
        private readonly HttpClient http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        private readonly List<Uri> history = new List<Uri>();
        private int historyIndex = -1;
        private CancellationTokenSource? inFlightCts;

        public MainForm()
        {
            Text = "Mini WinForms Browser";
            Width = 1100;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            InitializeLayout();
            InitializeIdleState();
            WireEvents();
        }

        // ------------------ UI LAYOUT ------------------
        private void InitializeLayout()
        {
            // Top tool row
            btnBack.Text = "◀";
            btnForward.Text = "▶";
            btnReload.Text = "⟳";
            btnGo.Text = "Go";

            btnBack.SetBounds(10, 10, 36, 28);
            btnForward.SetBounds(52, 10, 36, 28);
            btnReload.SetBounds(94, 10, 36, 28);
            txtAddress.SetBounds(140, 10, 770, 28);
            btnGo.SetBounds(920, 10, 60, 28);

            // Title label
            lblTitle.AutoSize = false;
            lblTitle.Text = "Title: —";
            lblTitle.SetBounds(10, 48, 970, 24);
            lblTitle.BorderStyle = BorderStyle.None;

            // Top-5 links list (left)
            lstTopLinks.SetBounds(10, 80, 500, 520);
            lstTopLinks.HorizontalScrollbar = true;

            // Raw HTML (right)
            txtRawHtml.Multiline = true;
            txtRawHtml.ScrollBars = ScrollBars.Both;
            txtRawHtml.WordWrap = false;
            txtRawHtml.ReadOnly = true;
            txtRawHtml.SetBounds(520, 80, 560, 520);

            // Status strip
            statusStrip.SizingGrip = false;
            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true }); // spacer
            statusStrip.Items.Add(httpStatusLabel);
            statusStrip.Dock = DockStyle.Bottom;

            // Add controls
            Controls.Add(btnBack);
            Controls.Add(btnForward);
            Controls.Add(btnReload);
            Controls.Add(txtAddress);
            Controls.Add(btnGo);
            Controls.Add(lblTitle);
            Controls.Add(lstTopLinks);
            Controls.Add(txtRawHtml);
            Controls.Add(statusStrip);
        }

        private void InitializeIdleState()
        {
            // Idle visuals
            statusLabel.Text = "Ready";
            httpStatusLabel.Text = "";
            btnBack.Enabled = false;
            btnForward.Enabled = false;
            btnReload.Enabled = false;

            // Helpful placeholder
            txtAddress.PlaceholderText = "Enter URL (e.g., https://example.com) and press Enter or Go";
            lstTopLinks.Items.Clear();
            lstTopLinks.Items.Add("Top-5 links will appear here after a successful request…");
            txtRawHtml.Text = string.Empty;
        }

        private void WireEvents()
        {
            btnGo.Click += async (_, __) => await NavigateFromAddressBarAsync();
            txtAddress.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await NavigateFromAddressBarAsync();
                }
            };
            btnBack.Click += (_, __) => NavigateHistory(-1);
            btnForward.Click += (_, __) => NavigateHistory(+1);
            btnReload.Click += async (_, __) => await ReloadAsync();

            lstTopLinks.DoubleClick += async (_, __) =>
            {
                if (lstTopLinks.SelectedItem is LinkItem item && item.Uri != null)
                {
                    await NavigateAsync(item.Uri, addToHistory: true);
                }
            };
        }

        // ------------------ NAVIGATION ------------------
        private async Task NavigateFromAddressBarAsync()
        {
            if (string.IsNullOrWhiteSpace(txtAddress.Text)) return;

            if (!TryNormalizeUri(txtAddress.Text.Trim(), out var uri))
            {
                ShowError("Invalid URL. Try including https://");
                return;
            }

            await NavigateAsync(uri, addToHistory: true);
        }

        private async Task ReloadAsync()
        {
            if (historyIndex < 0 || historyIndex >= history.Count) return;
            var current = history[historyIndex];
            await NavigateAsync(current, addToHistory: false);
        }

        private void NavigateHistory(int delta)
        {
            var newIndex = historyIndex + delta;
            if (newIndex < 0 || newIndex >= history.Count) return;

            historyIndex = newIndex;
            _ = NavigateAsync(history[historyIndex], addToHistory: false);
        }

        private async Task NavigateAsync(Uri uri, bool addToHistory)
        {
            // Cancel any in-flight request
            inFlightCts?.Cancel();
            inFlightCts = new CancellationTokenSource();

            try
            {
                SetLoading(true, $"Loading {uri} …");

                // HTTP GET
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, inFlightCts.Token);
                httpStatusLabel.Text = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                var bytes = await resp.Content.ReadAsByteArrayAsync(inFlightCts.Token);

                // Decode content (fallbacks)
                var encoding = TryGetEncoding(resp) ?? Encoding.UTF8;
                var html = encoding.GetString(bytes);

                // Update UI
                txtRawHtml.Text = html;

                // Parse Title & Top-5 links
                var (title, links) = ExtractTitleAndTopLinks(uri, html, topN: 5);
                lblTitle.Text = $"Title: {title}";
                PopulateLinksList(links);

                // Update address bar to resolved (useful after redirects)
                txtAddress.Text = resp.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();

                // History management
                if (addToHistory)
                {
                    // If we navigated after going back, truncate forward history:
                    if (historyIndex < history.Count - 1)
                        history.RemoveRange(historyIndex + 1, history.Count - (historyIndex + 1));

                    if (history.Count == 0 || history[^1] != uri)
                        history.Add(uri);

                    historyIndex = history.Count - 1;
                }

                UpdateNavButtons();
                SetLoading(false, "Done");
            }
            catch (OperationCanceledException)
            {
                SetLoading(false, "Canceled");
            }
            catch (Exception ex)
            {
                ShowError($"Navigation failed: {ex.Message}");
            }
        }

        // ------------------ PARSING ------------------
        private static (string Title, List<LinkItem> Links) ExtractTitleAndTopLinks(Uri baseUri, string html, int topN = 5)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Title
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            var title = titleNode?.InnerText?.Trim();
            if (string.IsNullOrEmpty(title))
                title = "(no <title> found)";

            // Anchor tags with href
            var anchors = doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null);

            // Deduplicate + normalize + simple quality filter
            var items = new List<LinkItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in anchors)
            {
                var href = a.GetAttributeValue("href", "").Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;

                // Ignore fragment-only and mailto/tel
                if (href.StartsWith("#")) continue;
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;

                // Resolve relative URLs
                Uri? resolved = null;
                try
                {
                    resolved = new Uri(baseUri, href);
                }
                catch
                {
                    continue; // skip malformed
                }

                // Deduplicate by absolute URL
                var key = resolved.AbsoluteUri;
                if (!seen.Add(key)) continue;

                // Visible text
                var text = HttpUtility.HtmlDecode(a.InnerText ?? "").Trim();
                if (string.IsNullOrEmpty(text))
                    text = resolved.Host; // fallback label

                items.Add(new LinkItem { Text = text, Uri = resolved });
                if (items.Count >= Math.Max(1, topN)) break;
            }

            return (title, items);
        }

        private void PopulateLinksList(List<LinkItem> links)
        {
            lstTopLinks.Items.Clear();

            if (links.Count == 0)
            {
                lstTopLinks.Items.Add("(No links found)");
                return;
            }

            foreach (var li in links)
            {
                lstTopLinks.Items.Add(li);
            }

            // Show tooltip via ToString override
        }

        // ------------------ HELPERS ------------------
        private static bool TryNormalizeUri(string input, out Uri uri)
        {
            if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                input = "https://" + input;
            }
            return Uri.TryCreate(input, UriKind.Absolute, out uri!);
        }

        private static Encoding? TryGetEncoding(HttpResponseMessage resp)
        {
            try
            {
                var charset = resp.Content.Headers.ContentType?.CharSet;
                if (!string.IsNullOrWhiteSpace(charset))
                {
                    return Encoding.GetEncoding(charset);
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private void UpdateNavButtons()
        {
            btnBack.Enabled = historyIndex > 0;
            btnForward.Enabled = historyIndex >= 0 && historyIndex < history.Count - 1;
            btnReload.Enabled = historyIndex >= 0;
        }

        private void SetLoading(bool loading, string message)
        {
            statusLabel.Text = message;
            Cursor = loading ? Cursors.AppStarting : Cursors.Default;
            btnGo.Enabled = !loading;
            btnReload.Enabled = !loading && historyIndex >= 0;
        }

        private void ShowError(string message)
        {
            statusLabel.Text = "Error";
            httpStatusLabel.Text = "";
            MessageBox.Show(this, message, "Navigation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // For ListBox display
        private sealed class LinkItem
        {
            public string Text { get; set; } = "";
            public Uri? Uri { get; set; }

            public override string ToString()
            {
                // Show both label and URL
                return Uri == null ? Text : $"{Text}  —  {Uri}";
            }
        }
    }
}
