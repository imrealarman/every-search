using System.IO.Compression;

namespace EverySearch.Tests;

public class EngineTests
{
    private static readonly long Now = DateTime.UtcNow.Ticks;

    private static Entry E(string dir, string name, long size = 1000, long? mtime = null, byte flags = 0) =>
        new() { Dir = dir, Name = name, Size = size, Mtime = mtime ?? Now, Flags = flags };

    private static Entry Folder(string dir, string name, byte flags = 0) => E(dir, name, 0, null, (byte)(1 | flags));

    private static Query Q(string text, MatchMode mode = MatchMode.Contains, TypeKind types = TypeKind.None, string exts = "",
        string[]? scope = null, bool includeSystem = false, bool caseSensitive = false, bool matchPath = false) =>
        Query.Parse(text, mode, caseSensitive, matchPath, types, exts, scope ?? Array.Empty<string>(), includeSystem);

    private static List<string> Names(IEnumerable<Entry> entries) => entries.Select(e => e.Name).ToList();

    private static FileIndex IndexOf(params Entry[] entries)
    {
        var index = new FileIndex();
        index.ReplaceAll(entries);
        return index;
    }

    private static List<Entry> Run(FileIndex index, Query q, int limit = 50, Func<Entry, long>? bonus = null) =>
        index.Search(q, limit, CancellationToken.None, bonus).Top;

    // ---- the "9864" problem: work files first, system noise hidden ---------------------------------------------

    [Fact]
    public void System_noise_is_hidden_by_default_and_ranked_last_when_shown()
    {
        var index = IndexOf(
            E(@"C:\Users\me\Documents", "report_9864.docx"),
            E(@"C:\Windows\WinSxS\Manifests", "amd64_9864_abc.manifest", flags: 8),
            E(@"C:\Windows\System32", "9864.dll", flags: 8),
            E(@"D:\.pnpm-store\files", new string('a', 31) + "9864", flags: 8),
            E(@"C:\Users\me\AppData\Local\Cache", "9864", flags: 8));

        Assert.Equal(new[] { "report_9864.docx" }, Names(Run(index, Q("9864"))));

        var all = Names(Run(index, Q("9864", includeSystem: true)));
        Assert.Equal(5, all.Count);
        Assert.Equal("report_9864.docx", all[0]);
    }

    [Fact]
    public void Hash_named_blobs_and_system_extensions_are_hidden_even_outside_system_folders()
    {
        var index = IndexOf(
            E(@"D:\data", new string('f', 40)),
            E(@"D:\data", "driver9864.sys"),
            E(@"D:\data", "photo9864.jpg"));
        Assert.Equal(new[] { "photo9864.jpg" }, Names(Run(index, Q("9864"))));
    }

    [Fact]
    public void Work_file_types_outrank_other_types_even_with_a_weaker_name_match()
    {
        var index = IndexOf(
            E(@"D:\x", "invoice.xyz"),                // exact-ish name, unknown type
            E(@"D:\x", "my old invoice scan.pdf"),    // contains, but a document
            E(@"D:\x", "invoice.cs"));                // code
        var order = Names(Run(index, Q("invoice")));
        Assert.Equal("my old invoice scan.pdf", order[0]);
        Assert.Equal("invoice.xyz", order[2]);
    }

    [Fact]
    public void Explicit_extension_or_scope_includes_system_files()
    {
        var index = IndexOf(E(@"C:\Windows\System32", "kernel32.dll", flags: 8));
        Assert.Empty(Run(index, Q("kernel")));
        Assert.Single(Run(index, Q("kernel", exts: "dll")));
        Assert.Single(Run(index, Q("kernel", scope: new[] { @"C:\Windows" })));
    }

    // ---- name ranking ------------------------------------------------------------------------------------------

    [Fact]
    public void Whole_name_beats_prefix_beats_word_start_beats_contains()
    {
        var index = IndexOf(
            E(@"D:\x", "xinvoicex.pdf"),
            E(@"D:\x", "my invoice.pdf"),
            E(@"D:\x", "invoice final.pdf"),
            E(@"D:\x", "invoice.pdf"));
        Assert.Equal(new[] { "invoice.pdf", "invoice final.pdf", "my invoice.pdf", "xinvoicex.pdf" }, Names(Run(index, Q("invoice"))));
    }

    [Fact]
    public void Opened_files_rise_above_equally_good_matches()
    {
        var a = E(@"D:\x", "budget a.xlsx");
        var b = E(@"D:\x", "budget b.xlsx");
        var index = IndexOf(a, b);
        long Bonus(Entry e) => e.Name == "budget b.xlsx" ? 3_000_000_000 : 0;
        Assert.Equal("budget b.xlsx", Run(index, Q("budget"), bonus: Bonus)[0].Name);
    }

    [Fact]
    public void Usage_bonus_never_lifts_a_system_file_above_a_document()
    {
        var store = new UsageStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        for (int i = 0; i < 50; i++) store.Record(@"C:\Windows\x\budget.dll");
        var index = IndexOf(E(@"C:\Windows\x", "budget.dll", flags: 8), E(@"D:\x", "budget.docx"));
        var order = Names(Run(index, Q("budget", includeSystem: true), bonus: e => store.Bonus(Path.Join(e.Dir, e.Name))));
        Assert.Equal("budget.docx", order[0]);
    }

    // ---- filters -----------------------------------------------------------------------------------------------

    [Fact]
    public void Several_types_and_extensions_combine()
    {
        var index = IndexOf(
            E(@"D:\x", "a.pdf"), E(@"D:\x", "b.png"), E(@"D:\x", "c.mp4"), E(@"D:\x", "d.dwg"), E(@"D:\x", "e.zip"), Folder(@"D:\x", "f"));
        var docsAndImages = Names(Run(index, Q("", types: TypeKind.Documents | TypeKind.Images)));
        Assert.Equal(new[] { "a.pdf", "b.png" }, docsAndImages.OrderBy(n => n));
        Assert.Equal(new[] { "d.dwg" }, Names(Run(index, Q("", types: TypeKind.Cad))));
        Assert.Equal(new[] { "c.mp4", "e.zip" }, Names(Run(index, Q("", exts: "mp4, zip"))).OrderBy(n => n));
        Assert.Equal(new[] { "f" }, Names(Run(index, Q("", types: TypeKind.Folders))));
        Assert.Equal(new[] { "b.png", "d.dwg" }, Names(Run(index, Q("", types: TypeKind.Images, exts: "dwg"))).OrderBy(n => n));
    }

    [Fact]
    public void Operators_in_the_text_work_like_the_filter_bar()
    {
        var index = IndexOf(E(@"D:\x", "a.pdf", 5_000_000), E(@"D:\x", "b.pdf", 100), E(@"D:\x", "c.mkv", 9_000_000));
        Assert.Equal(new[] { "a.pdf" }, Names(Run(index, Q("ext:pdf size:>1mb"))));
        Assert.Equal(new[] { "c.mkv" }, Names(Run(index, Q("type:video"))));
        Assert.Equal(new[] { "b.pdf" }, Names(Run(index, Q("size:<1kb"))));
        Assert.Equal(2, Run(index, Q("size:100..6mb")).Count);
    }

    [Fact]
    public void Scope_limits_results_to_the_chosen_drives_and_folders()
    {
        var index = IndexOf(
            E(@"C:\Users\me", "notes.txt"), E(@"D:\work\p1", "notes.txt"), E(@"E:\usb", "notes.txt"), E(@"D:\workshop", "notes.txt"));
        Assert.Equal(4, Run(index, Q("notes")).Count);
        Assert.Equal(new[] { @"D:\work\p1" }, Run(index, Q("notes", scope: new[] { @"D:\work" })).Select(e => e.Dir));
        Assert.Equal(2, Run(index, Q("notes", scope: new[] { "C:", "E:" })).Count);
        Assert.Single(Run(index, Q("notes", scope: new[] { @"D:\work\" })));
        Assert.Single(Run(index, Q("notes in:E:\\usb")));
    }

    [Fact]
    public void Modified_filter_understands_relative_and_absolute_dates()
    {
        var day = TimeSpan.TicksPerDay;
        var index = IndexOf(
            E(@"D:\x", "today.txt", mtime: Now),
            E(@"D:\x", "lastweek.txt", mtime: Now - 5 * day),
            E(@"D:\x", "old.txt", mtime: new DateTime(2020, 3, 10, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks));
        Assert.Equal(new[] { "today.txt" }, Names(Run(index, Q("modified:today"))));
        Assert.Equal(2, Run(index, Q("modified:7d")).Count);
        Assert.Equal(new[] { "old.txt" }, Names(Run(index, Q("modified:2020-03-10"))));
        Assert.Equal(new[] { "old.txt" }, Names(Run(index, Q("modified:<2021-01-01"))));
        Assert.Equal(new[] { "old.txt" }, Names(Run(index, Q("dm:2020-01-01..2020-12-31"))));
        Assert.Equal(2, Run(index, Q("modified:>2021-01-01")).Count);
    }

    [Fact]
    public void Folder_and_file_operators()
    {
        var index = IndexOf(Folder(@"D:\x", "assets"), E(@"D:\x", "assets.txt"));
        Assert.Equal(new[] { "assets" }, Names(Run(index, Q("folder:assets"))));
        Assert.Equal(new[] { "assets.txt" }, Names(Run(index, Q("file:assets"))));
    }

    // ---- pattern modes -----------------------------------------------------------------------------------------

    [Fact]
    public void Pattern_modes()
    {
        var index = IndexOf(E(@"D:\x", "Report 2024.docx"), E(@"D:\x", "report.pdf"), E(@"D:\x", "main.cs"), E(@"D:\x", "readme.md"));
        Assert.Equal(new[] { "report.pdf" }, Names(Run(index, Q("report", MatchMode.Exact))));
        Assert.Equal(new[] { "main.cs" }, Names(Run(index, Q("m*.cs", MatchMode.Wildcard))));
        Assert.Equal(new[] { "Report 2024.docx" }, Names(Run(index, Q(@"\d{4}", MatchMode.Regex))));
        Assert.Contains("readme.md", Names(Run(index, Q("rdm", MatchMode.Fuzzy))));
        Assert.Empty(Run(index, Q("REPORT", caseSensitive: true)));
        Assert.Equal(2, Run(index, Q("REPORT")).Count);
    }

    [Fact]
    public void Invalid_regex_reports_an_error_instead_of_throwing()
    {
        Assert.NotNull(Q("(", MatchMode.Regex).Error);
    }

    [Fact]
    public void Negation_phrases_and_path_matching()
    {
        var index = IndexOf(E(@"D:\Taxes", "form a.pdf"), E(@"D:\Taxes", "form b.pdf"), E(@"D:\Other", "form a.pdf"));
        Assert.Equal(new[] { "form b.pdf" }, Names(Run(index, Q("form !a"))));
        Assert.Equal(2, Run(index, Q("\"form a\"")).Count);
        Assert.Equal(2, Run(index, Q("form taxes", matchPath: true)).Count);
        Assert.Equal(2, Run(index, Q("path:taxes")).Count);
        Assert.Equal(2, Run(index, Q(@"taxes\form")).Count);
    }

    [Fact]
    public void Highlight_terms_follow_the_name_words_only()
    {
        Assert.Equal(new[] { "invoice", "2024" }, Q("invoice 2024 !draft ext:pdf").HighlightTerms);
        Assert.Empty(Q("a*", MatchMode.Wildcard).HighlightTerms);
    }

    [Fact]
    public void Content_operator_is_parsed_and_counts_as_a_filter()
    {
        var q = Q("content:\"hello world\"");
        Assert.Equal("hello world", q.ContentText);
        Assert.True(q.HasFilter);
    }

    // ---- file types --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a.PDF", TypeKind.Documents)]
    [InlineData("a.docx", TypeKind.Documents)]
    [InlineData("a.jpeg", TypeKind.Images)]
    [InlineData("a.mkv", TypeKind.Video)]
    [InlineData("a.mp3", TypeKind.Audio)]
    [InlineData("a.dwg", TypeKind.Cad)]
    [InlineData("a.7z", TypeKind.Archives)]
    [InlineData("a.ts", TypeKind.Code)]
    [InlineData("a.exe", TypeKind.Executables)]
    [InlineData("a.dll", TypeKind.System)]
    [InlineData("a.unknownext", TypeKind.Other)]
    [InlineData("noextension", TypeKind.Other)]
    [InlineData(".gitignore", TypeKind.Other)]
    public void Extensions_map_to_kinds(string name, TypeKind expected) =>
        Assert.Equal(expected, FileTypes.KindOfEntry(name, false));

    [Fact]
    public void Every_chip_has_an_icon_file()
    {
        foreach (var (kind, _, _) in FileTypes.All)
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets", "Types", FileTypes.IconFile(kind))), kind.ToString());
    }

    // ---- crawling, snapshot, usage, content search -------------------------------------------------------------

    [Fact]
    public async Task Crawl_flags_system_folders_and_snapshot_round_trips()
    {
        string root = Path.Combine(Path.GetTempPath(), "es-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "x");
        File.WriteAllText(Path.Combine(root, "docs", "plan.docx"), "x");
        try
        {
            var index = new FileIndex();
            await index.ScanAsync(new[] { root }, Array.Empty<string>());

            Assert.Equal(new[] { "plan.docx" }, Names(Run(index, Q("plan"))));
            Assert.Empty(Run(index, Q("index.js")));
            Assert.Single(Run(index, Q("index.js", includeSystem: true)));

            index.SaveSnapshot(new[] { root });
            var reloaded = new FileIndex();
            Assert.True(reloaded.TryLoadSnapshot(new[] { root }));
            Assert.Equal(index.Count, reloaded.Count);
            Assert.False(new FileIndex().TryLoadSnapshot(new[] { root + "-other" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Usage_store_persists_and_ranks_by_count_with_a_cap()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new UsageStore(file);
        Assert.Equal(0, store.Bonus(@"D:\x\a.txt"));
        store.Record(@"D:\x\a.txt");
        long once = store.Bonus(@"D:\X\A.TXT"); // paths compare case-insensitively
        for (int i = 0; i < 30; i++) store.Record(@"D:\x\a.txt");
        Assert.True(once > 0);
        Assert.True(store.Bonus(@"D:\x\a.txt") > once);
        Assert.True(store.Bonus(@"D:\x\a.txt") < 10_000_000_000); // never crosses a file-type tier
        Assert.True(new UsageStore(file).Bonus(@"D:\x\a.txt") > 0);
        store.Clear();
        Assert.Equal(0, store.Bonus(@"D:\x\a.txt"));
    }

    [Fact]
    public async Task Content_search_reads_text_and_office_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "es-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "line one\nthe quarterly Budget figures\nline three");
            File.WriteAllText(Path.Combine(dir, "other.txt"), "nothing here");
            File.WriteAllBytes(Path.Combine(dir, "binary.txt"), new byte[] { 0, 1, 2, 3, 0, 0 });
            using (var zip = ZipFile.Open(Path.Combine(dir, "memo.docx"), ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                w.Write("<w:document><w:p><w:t>Please review the budget</w:t></w:p></w:document>");

            var candidates = Directory.GetFiles(dir).Select(p => E(dir, Path.GetFileName(p), new FileInfo(p).Length)).ToList();
            var result = await ContentSearch.SearchAsync(candidates, "budget", false, 100, TimeSpan.FromSeconds(10), null, CancellationToken.None);

            Assert.Equal(new[] { "memo.docx", "notes.txt" }, result.Hits.Select(h => h.Entry.Name).OrderBy(n => n));
            Assert.Contains("quarterly Budget figures", result.Hits.Single(h => h.Entry.Name == "notes.txt").Snippet);

            var caseSensitive = await ContentSearch.SearchAsync(candidates, "budget", true, 100, TimeSpan.FromSeconds(10), null, CancellationToken.None);
            Assert.Equal(new[] { "memo.docx" }, caseSensitive.Hits.Select(h => h.Entry.Name));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Re_created_and_added_entries_do_not_duplicate()
    {
        var index = IndexOf(E(@"D:\x", "a.txt"));
        Assert.Single(Run(index, Q("a.txt")));
        Assert.Equal(1, index.Count);
    }
}
