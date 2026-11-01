using System.Text;
using System.Text.RegularExpressions;

namespace EverySearch;

public enum MatchMode { Contains, Exact, Wildcard, Regex, Fuzzy }

/// <summary>
/// A parsed search. Combines the typed text (with inline operators such as <c>ext:pdf</c>, <c>type:image,video</c>,
/// <c>size:&gt;10mb</c>, <c>path:docs</c>, <c>folder:</c>) with the options chosen in the filter bar.
/// Lower <c>key</c> values rank higher.
/// </summary>
public sealed class Query
{
    private readonly record struct Term(string Text, bool Negate, bool PathOnly, bool HasSlash);

    public MatchMode Mode { get; private set; }
    public bool CaseSensitive { get; private set; }
    public bool MatchPath { get; private set; }
    /// <summary>False (the default) hides hidden/system files and anything inside Windows, Program Files, AppData, node_modules and similar.</summary>
    public bool IncludeSystem { get; private set; }
    public TypeKind Types { get; private set; }
    public HashSet<string> Exts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long MinSize { get; private set; } = -1;
    public long MaxSize { get; private set; } = -1;
    public bool FilesOnly { get; private set; }
    public bool FoldersOnly { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Folders/drives the search is limited to (no trailing backslash). Empty means the entire PC.</summary>
    public string[] Scope { get; private set; } = Array.Empty<string>();
    /// <summary>Modified-time bounds in UTC ticks (-1 = unbounded), from the modified: operator.</summary>
    public long MinMtime { get; private set; } = -1;
    public long MaxMtime { get; private set; } = -1;
    /// <summary>Text to look for inside file contents (content: operator). Null for a normal name search.</summary>
    public string? ContentText { get; private set; }
    /// <summary>The words to emphasise in result names.</summary>
    public string[] HighlightTerms => Mode switch
    {
        MatchMode.Contains => terms.Where(t => !t.Negate && !t.PathOnly && !t.HasSlash).Select(t => t.Text).ToArray(),
        MatchMode.Exact => pattern.Length > 0 ? new[] { pattern } : Array.Empty<string>(),
        _ => Array.Empty<string>(),
    };

    private readonly List<Term> terms = new();
    private readonly List<string> inScopes = new();
    private string pattern = "";
    private Regex? regex;
    private StringComparison cmp;

    public bool HasText => Mode == MatchMode.Contains ? terms.Count > 0 : pattern.Length > 0;
    public bool KindActive => Types != TypeKind.None || Exts.Count > 0;
    public bool HasFilter => Scope.Length > 0 || ContentText != null || MinMtime >= 0 || MaxMtime >= 0 || KindActive || MinSize >= 0 || MaxSize >= 0 || FilesOnly || FoldersOnly;

    public static Query Parse(string input, MatchMode mode, bool caseSensitive, bool matchPath, TypeKind uiTypes, string uiExts, IEnumerable<string> uiScope, bool includeSystem = false)
    {
        var q = new Query
        {
            Mode = mode,
            CaseSensitive = caseSensitive,
            MatchPath = matchPath,
            Types = uiTypes,
            IncludeSystem = includeSystem,
            cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
        };
        q.AddExtensions(uiExts);

        var words = new List<string>();
        foreach (var (token, quoted) in Tokenize(input))
        {
            if (!quoted && q.TryOperator(token, words)) continue;
            words.Add(quoted ? "\"" + token : token); // a leading quote marks a phrase so "!" and ":" stay literal
        }

        // An in: operator in the text overrides the scope picked in the UI.
        var scope = q.inScopes.Count > 0 ? q.inScopes : uiScope;
        q.Scope = scope.Select(p => p.Trim().TrimEnd('\\', '/')).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (mode == MatchMode.Contains)
        {
            foreach (var w in words)
            {
                bool phrase = w.StartsWith('"');
                string text = phrase ? w[1..] : w;
                bool negate = !phrase && text.Length > 1 && text[0] == '!';
                if (negate) text = text[1..];
                if (text.Length == 0) continue;
                bool slash = text.Contains('\\') || text.Contains('/');
                if (slash) text = text.Replace('/', '\\');
                q.terms.Add(new Term(text, negate, false, slash));
            }
        }
        else
        {
            q.pattern = string.Join(' ', words.Select(w => w.TrimStart('"')));
            q.Compile();
        }
        return q;
    }

    private void Compile()
    {
        if (pattern.Length == 0) return;
        var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            if (Mode == MatchMode.Wildcard)
            {
                string body = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
                regex = new Regex("^" + body + "$", opts);
            }
            else if (Mode == MatchMode.Regex)
            {
                regex = new Regex(pattern, opts);
            }
        }
        catch (ArgumentException ex)
        {
            Error = "Invalid pattern: " + ex.Message;
        }
    }

    // ---- parsing -----------------------------------------------------------------------------------------------

    private static IEnumerable<(string Token, bool Quoted)> Tokenize(string s)
    {
        var sb = new StringBuilder();
        bool inQuote = false, wasQuoted = false;
        foreach (char c in s)
        {
            if (c == '"') { if (!inQuote && sb.Length == 0) wasQuoted = true; inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0) yield return (sb.ToString(), wasQuoted);
                sb.Clear();
                wasQuoted = false;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return (sb.ToString(), wasQuoted);
    }

    private bool TryOperator(string token, List<string> words)
    {
        int colon = token.IndexOf(':');
        if (colon <= 0 || token.Length > 1 && token[0] == '!') return false;
        string key = token[..colon].ToLowerInvariant();
        string value = token[(colon + 1)..];
        switch (key)
        {
            case "ext": AddExtensions(value); return true;
            case "type":
                foreach (var part in value.Split(',', ';', '|'))
                    if (FileTypes.TryParseKind(part, out var kind)) Types |= kind;
                return true;
            case "size": ParseSize(value); return true;
            case "modified" or "dm": ParseDate(value); return true;
            case "content" or "text":
                if (value.Length > 0) ContentText = value;
                return true;
            case "in":
                if (value.Length > 0) inScopes.Add(value.Replace('/', '\\'));
                return true;
            case "path":
                if (value.Length > 0) terms.Add(new Term(value.Replace('/', '\\'), false, true, false));
                return true;
            case "file" or "files":
                FilesOnly = true;
                if (value.Length > 0) words.Add(value);
                return true;
            case "folder" or "folders":
                FoldersOnly = true;
                if (value.Length > 0) words.Add(value);
                return true;
            default: return false;
        }
    }

    private void AddExtensions(string list)
    {
        foreach (var part in list.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = part.Trim().TrimStart('*').TrimStart('.');
            if (ext.Length > 0) Exts.Add(ext);
        }
    }

    /// <summary>modified:today|yesterday|week|month|year, 7d/2w/3m/1y (last N), &gt;2026-01-01, &lt;=2026-03-01, 2026-01-01..2026-02-01, or one day.</summary>
    private void ParseDate(string v)
    {
        var today = DateTime.Today;
        long Utc(DateTime d) => d.ToUniversalTime().Ticks;
        long EndOfDay(DateTime d) => Utc(d.Date.AddDays(1)) - 1;
        static bool TryDate(string s, out DateTime d) =>
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out d);

        switch (v.ToLowerInvariant())
        {
            case "today": MinMtime = Utc(today); return;
            case "yesterday": MinMtime = Utc(today.AddDays(-1)); MaxMtime = Utc(today) - 1; return;
            case "week": MinMtime = Utc(today.AddDays(-7)); return;
            case "month": MinMtime = Utc(today.AddMonths(-1)); return;
            case "year": MinMtime = Utc(today.AddYears(-1)); return;
        }

        var last = Regex.Match(v, @"^(\d+)([dwmy])$", RegexOptions.IgnoreCase);
        if (last.Success)
        {
            int n = int.Parse(last.Groups[1].Value);
            MinMtime = Utc(char.ToLowerInvariant(last.Groups[2].Value[0]) switch
            {
                'd' => today.AddDays(-n),
                'w' => today.AddDays(-7 * n),
                'm' => today.AddMonths(-n),
                _ => today.AddYears(-n),
            });
            return;
        }

        int range = v.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (TryDate(v[..range], out var from)) MinMtime = Utc(from.Date);
            if (TryDate(v[(range + 2)..], out var to)) MaxMtime = EndOfDay(to);
            return;
        }

        string op = v.StartsWith(">=") ? ">=" : v.StartsWith("<=") ? "<=" : v.Length > 0 && "<>".Contains(v[0]) ? v[..1] : "";
        if (!TryDate(v[op.Length..], out var date)) return;
        switch (op)
        {
            case ">": MinMtime = Utc(date.Date.AddDays(1)); break;
            case ">=": MinMtime = Utc(date.Date); break;
            case "<": MaxMtime = Utc(date.Date) - 1; break;
            case "<=": MaxMtime = EndOfDay(date); break;
            default: MinMtime = Utc(date.Date); MaxMtime = EndOfDay(date); break;
        }
    }

    private void ParseSize(string v)
    {
        int range = v.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (TryBytes(v[..range], out var lo)) MinSize = lo;
            if (TryBytes(v[(range + 2)..], out var hi)) MaxSize = hi;
            return;
        }
        string op = v.StartsWith(">=") ? ">=" : v.StartsWith("<=") ? "<=" : v.Length > 0 && "<>=".Contains(v[0]) ? v[..1] : "=";
        if (!TryBytes(op == "=" && !v.StartsWith('=') ? v : v[op.Length..], out var n)) return;
        switch (op)
        {
            case ">": MinSize = n + 1; break;
            case ">=": MinSize = n; break;
            case "<": MaxSize = Math.Max(0, n - 1); break;
            case "<=": MaxSize = n; break;
            default: MinSize = n; MaxSize = n; break;
        }
    }

    private static bool TryBytes(string s, out long bytes)
    {
        bytes = 0;
        s = s.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        long mult = 1;
        foreach (var (suffix, m) in new[] { ("tb", 1L << 40), ("gb", 1L << 30), ("mb", 1L << 20), ("kb", 1L << 10), ("b", 1L) })
        {
            if (s.EndsWith(suffix)) { mult = m; s = s[..^suffix.Length]; break; }
        }
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return false;
        bytes = (long)(d * mult);
        return true;
    }

    // ---- matching ----------------------------------------------------------------------------------------------

    /// <summary>True when the entry matches; <paramref name="key"/> orders results (lower is better).</summary>
    public bool Match(in Entry e, out long key)
    {
        key = 0;
        bool isDir = (e.Flags & 1) != 0;
        if (FilesOnly && isDir) return false;
        if (FoldersOnly && !isDir) return false;
        if (Scope.Length > 0 && !InScope(e.Dir)) return false;
        if (MinMtime >= 0 && e.Mtime < MinMtime) return false;
        if (MaxMtime >= 0 && e.Mtime > MaxMtime) return false;
        if (MinSize >= 0 || MaxSize >= 0)
        {
            if (isDir) return false;
            if (MinSize >= 0 && e.Size < MinSize) return false;
            if (MaxSize >= 0 && e.Size > MaxSize) return false;
        }

        bool hasText = HasText;
        if (hasText && !TextMatch(e, out key)) return false;
        if (!hasText) key = -e.Mtime; // no text: newest first

        // Only entries that passed the text test get here, so the extension lookup runs on far fewer entries.
        var kind = FileTypes.KindOfEntry(e.Name, isDir);
        bool junk = (e.Flags & SystemFlags) != 0 || kind == TypeKind.System || IsHashName(e.Name, kind);
        // Asking for a scope or an explicit extension means the user wants those files, system or not.
        if (junk && !IncludeSystem && Scope.Length == 0 && Exts.Count == 0) return false;
        if (KindActive && !KindMatches(e, isDir, kind)) return false;

        // Rank by what people work with first (documents, media, CAD), then by how well the name matches.
        if (hasText) key += (junk ? 5 : FileTypes.TierOf(kind)) * 10_000_000_000L;
        return true;
    }

    /// <summary>Extensionless 32+ character hex names are package-store/cache blobs, never something a person named.</summary>
    private static bool IsHashName(string name, TypeKind kind)
    {
        if (kind != TypeKind.Other || name.Length < 32) return false;
        foreach (char c in name)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Flags bits 2 and 3: hidden/system attribute, or located inside a system or build folder.</summary>
    public const int SystemFlags = 4 | 8;

    private bool InScope(string dir)
    {
        foreach (var p in Scope)
            if (dir.StartsWith(p, StringComparison.OrdinalIgnoreCase) && (dir.Length == p.Length || dir[p.Length] == '\\'))
                return true;
        return false;
    }

    private bool KindMatches(in Entry e, bool isDir, TypeKind kind)
    {
        if (isDir) return (Types & TypeKind.Folders) != 0;
        if (Exts.Count > 0 && Exts.Contains(FileTypes.ExtensionOf(e.Name))) return true;
        var group = kind == TypeKind.System ? TypeKind.Other : kind; // system extensions count as "Other"
        return (Types & group) != 0;
    }

    private bool TextMatch(in Entry e, out long key)
    {
        key = 0;
        long nameLen = Math.Min(e.Name.Length, 999_999);
        switch (Mode)
        {
            case MatchMode.Contains:
            {
                int worst = 0;
                foreach (var t in terms)
                {
                    bool found;
                    int score;
                    if (t.PathOnly) { found = e.Dir.Contains(t.Text, cmp); score = 4; }
                    else if (t.HasSlash) { found = FullPathContains(e, t.Text); score = 4; }
                    else
                    {
                        score = ScoreName(e.Name, t.Text);
                        found = score >= 0;
                        if (!found && MatchPath) { found = e.Dir.Contains(t.Text, cmp); score = 4; }
                    }
                    if (t.Negate) { if (found) return false; continue; }
                    if (!found) return false;
                    if (score > worst) worst = score;
                }
                key = worst * 1_000_000L + nameLen;
                return true;
            }
            case MatchMode.Exact:
            {
                string name = e.Name;
                if (string.Equals(name, pattern, cmp)) { key = nameLen; return true; }
                int dot = name.LastIndexOf('.');
                if (dot > 0 && name.AsSpan(0, dot).Equals(pattern, cmp)) { key = nameLen; return true; }
                return false;
            }
            case MatchMode.Wildcard:
            case MatchMode.Regex:
            {
                if (regex == null) return false;
                bool ok = MatchPath ? regex.IsMatch(string.Concat(e.Dir, "\\", e.Name)) : regex.IsMatch(e.Name);
                key = 1_000_000L + nameLen;
                return ok;
            }
            case MatchMode.Fuzzy:
            {
                string name = e.Name;
                int pi = 0, start = -1, end = -1;
                for (int i = 0; i < name.Length; i++)
                {
                    if (!CharEq(name[i], pattern[pi])) continue;
                    if (pi == 0) start = i;
                    if (++pi == pattern.Length) { end = i; break; }
                }
                if (end < 0) return false;
                int gaps = end - start + 1 - pattern.Length;
                key = 1_000_000L + gaps * 1000L + Math.Min(nameLen, 999);
                return true;
            }
        }
        return false;
    }

    private bool CharEq(char a, char b) => CaseSensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>0 = whole name (or name without extension), 1 = prefix, 2 = starts a word, 3 = elsewhere, -1 = no match.</summary>
    private int ScoreName(string name, string text)
    {
        int i = name.IndexOf(text, cmp);
        if (i < 0) return -1;
        if (name.Length == text.Length) return 0;
        if (i == 0)
        {
            bool stem = name[text.Length] == '.' && name.IndexOf('.', text.Length + 1) < 0;
            return stem ? 0 : 1;
        }
        return char.IsLetterOrDigit(name[i - 1]) ? 3 : 2;
    }

    private bool FullPathContains(in Entry e, string term)
    {
        int slash = term.LastIndexOf('\\');
        string tail = term[(slash + 1)..];
        if (tail.Length > 0 && !e.Name.Contains(tail, cmp)) return false;
        return string.Concat(e.Dir, "\\", e.Name).Contains(term, cmp);
    }
}
