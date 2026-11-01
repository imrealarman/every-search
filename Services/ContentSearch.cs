using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace EverySearch;

public sealed record ContentHit(Entry Entry, string Snippet);

public sealed record ContentResult(List<ContentHit> Hits, int Searched, int Candidates, bool Truncated);

/// <summary>
/// Looks inside files for text. Reads plain-text and code files, and the text of docx / pptx / xlsx. It scans the
/// candidate files on demand (nothing is indexed), so narrow it with a scope, type or name first.
/// </summary>
public static class ContentSearch
{
    private const int MaxFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "md", "log", "csv", "tsv", "ini", "cfg", "conf", "json", "xml", "yml", "yaml", "toml", "srt", "html", "htm", "css", "tex" };
    private static readonly HashSet<string> OfficeExts = new(StringComparer.OrdinalIgnoreCase) { "docx", "pptx", "xlsx" };

    public static bool IsSearchable(string name)
    {
        var ext = FileTypes.ExtensionOf(name);
        return TextExts.Contains(ext) || OfficeExts.Contains(ext) || FileTypes.KindOf(ext) == TypeKind.Code;
    }

    public static async Task<ContentResult> SearchAsync(
        IReadOnlyList<Entry> candidates, string text, bool caseSensitive, int maxHits, TimeSpan budget,
        IProgress<int>? progress, CancellationToken ct)
    {
        var files = candidates.Where(e => (e.Flags & 1) == 0 && e.Size <= MaxFileBytes * 8 && IsSearchable(e.Name)).ToList();
        var hits = new ConcurrentBag<ContentHit>();
        int searched = 0;
        bool truncated = false;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(budget);
        var options = new ParallelOptions { CancellationToken = stop.Token, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };

        await Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(files, options, (entry, state) =>
                {
                    if (hits.Count >= maxHits) { truncated = true; state.Stop(); return; }
                    string? content = ReadText(Path.Join(entry.Dir, entry.Name));
                    int n = Interlocked.Increment(ref searched);
                    if ((n & 0x3F) == 0) progress?.Report(n);
                    if (content == null) return;
                    int at = content.IndexOf(text, cmp);
                    if (at >= 0) hits.Add(new ContentHit(entry, Snippet(content, at, text.Length)));
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                truncated = true; // ran out of time budget; return what was found
            }
        }, CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        return new ContentResult(hits.OrderByDescending(h => h.Entry.Mtime).ToList(), searched, files.Count, truncated);
    }

    /// <summary>The surrounding text of a match, flattened to one line.</summary>
    public static string Snippet(string content, int at, int length)
    {
        int from = Math.Max(0, at - 50), to = Math.Min(content.Length, at + length + 70);
        string s = Regex.Replace(content[from..to], @"\s+", " ").Trim();
        return (from > 0 ? "…" : "") + s + (to < content.Length ? "…" : "");
    }

    private static string? ReadText(string path)
    {
        try
        {
            string ext = FileTypes.ExtensionOf(Path.GetFileName(path));
            return OfficeExts.Contains(ext) ? ReadOffice(path, ext) : ReadPlain(path);
        }
        catch { return null; } // locked, unreadable or corrupt: skip it
    }

    private static string? ReadPlain(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[(int)Math.Min(fs.Length, MaxFileBytes)];
        int n = fs.Read(buf, 0, buf.Length);
        if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 4096)) >= 0) return null; // binary
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static string? ReadOffice(string path, string ext)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            string n = entry.FullName;
            bool wanted = ext switch
            {
                "docx" => n == "word/document.xml",
                "xlsx" => n == "xl/sharedStrings.xml",
                _ => n.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal),
            };
            if (!wanted || entry.Length > MaxFileBytes * 4) continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            sb.Append(Regex.Replace(reader.ReadToEnd(), "<[^>]+>", " ")).Append(' ');
            if (sb.Length > MaxFileBytes) break;
        }
        return sb.Length == 0 ? null : System.Net.WebUtility.HtmlDecode(sb.ToString());
    }
}
