using System;
using System.Collections.Generic;
using System.IO;  // Added for Path
using System.Linq;  // Added for Select and Cast
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// HttpResult (Your class, unchanged)
public class HttpResult
{
    public int StatusCode { get; set; }              // 0 for transport/runtime errors
    public string ReasonPhrase { get; set; } = "";
    public string HtmlContent { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Links { get; set; } = new();
}

// HttpClientService (to fetch data and populate HttpResult)
public class HttpClientService
{
    private static readonly HttpClient client = new HttpClient();
    
    public async Task<HttpResult> GetAsync(Uri url)
    {
        var result = new HttpResult();
        try
        {
            HttpResponseMessage response = await client.GetAsync(url);
            result.StatusCode = (int)response.StatusCode;
            result.ReasonPhrase = response.ReasonPhrase ?? "OK";
            result.HtmlContent = await response.Content.ReadAsStringAsync();
            result.Title = HtmlInfoExtractor.TryGetTitle(result.HtmlContent);  // Extract title
            result.Links = HtmlInfoExtractor.ExtractFirstFiveLinks(result.HtmlContent, url)
                .Select(link => link.Href.ToString()).ToList();  // Extract links
            return result;
        }
        catch (Exception ex)
        {
            result.StatusCode = 500;
            result.ReasonPhrase = ex.Message;
            return result;
        }
    }
}

// HtmlInfoExtractor (static class for extraction)
public static class HtmlInfoExtractor
{
    private static readonly Regex TitleRegex = new Regex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LinkRegex = new Regex(@"<a\s+[^>]*href\s*=\s*""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static string TryGetTitle(string html)
    {
        if (string.IsNullOrEmpty(html)) return "(no title)";
        var match = TitleRegex.Match(html);
        if (match.Success && match.Groups.Count > 1)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }
        return "(no title)";
    }

    public static List<(string Text, Uri Href)> ExtractFirstFiveLinks(string html, Uri baseUri)
    {
        var links = new List<(string Text, Uri Href)>();
        if (string.IsNullOrEmpty(html)) return links;
        var matches = LinkRegex.Matches(html).Cast<Match>();  // Fixed with Cast from LINQ
        var processedLinks = new HashSet<string>();
        foreach (Match match in matches)
        {
            if (match.Groups.Count < 3) continue;
            string href = match.Groups[1].Value.Trim();
            string linkText = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(linkText)) continue;
            if (processedLinks.Contains(href)) continue;
            processedLinks.Add(href);
            Uri absoluteHref;
            if (Uri.TryCreate(href, UriKind.Absolute, out absoluteHref)) { }
            else { try { absoluteHref = new Uri(baseUri, href); } catch { continue; } }
            links.Add((linkText, absoluteHref));
            if (links.Count == 5) break;
        }
        return links;
    }
}

// Bookmark (for completeness)
public class Bookmark
{
    public string Name { get; set; } = string.Empty;
    public Uri Url { get; set; } = default!;
}

// BookmarkManager (for completeness)
public class BookmarkManager
{
    private List<Bookmark> bookmarks = new List<Bookmark>();
    private readonly string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bookmarks.json");
    public BookmarkManager() { }
    public void AddBookmark(string name, Uri url) { Console.WriteLine("Bookmark added!"); }  // Simplified
}

// History (for completeness)
public class History
{
    private List<Uri> historyList = new List<Uri>();
    private int currentIndex = -1;
    public void AddToHistory(Uri url) { if (url != null) { historyList.Add(url); currentIndex = historyList.Count - 1; } }
    public Uri GoBack() { if (currentIndex > 0) { currentIndex--; return historyList[currentIndex]; } return null; }
    public Uri GoForward() { if (currentIndex < historyList.Count - 1) { currentIndex++; return historyList[currentIndex]; } return null; }
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Simple Browser Demo (Console Version with HttpResult)\n");

        var httpService = new HttpClientService();
        Uri testUrl = new Uri("https://example.com");

        Console.WriteLine($"Fetching {testUrl}...");
        HttpResult result = await httpService.GetAsync(testUrl);

        Console.WriteLine("\n--- HttpResult Output ---");
        Console.WriteLine($"Status Code: {result.StatusCode}");
        Console.WriteLine($"Reason Phrase: {result.ReasonPhrase}");
        Console.WriteLine($"Title: {result.Title}");
        Console.WriteLine("Raw HTML (first 300 chars):");
        string htmlPreview = result.HtmlContent.Length > 300 ? result.HtmlContent.Substring(0, 300) + "..." : result.HtmlContent;
        Console.WriteLine(htmlPreview);
        Console.WriteLine("\nFirst 5 Links:");
        for (int i = 0; i < result.Links.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {result.Links[i]}");
        }

        Console.WriteLine("\nDemo complete. Press any key to exit...");
        Console.ReadKey();
    }
} 