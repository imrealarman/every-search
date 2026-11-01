using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace EverySearch;

/// <summary>One file or folder. <c>Dir</c> is a string shared by every sibling, which keeps millions of entries small.</summary>
public struct Entry
{
    public string Name;
    public string Dir;
    public long Size;
    public long Mtime;
    /// <summary>bit 0 = folder.</summary>
    public byte Flags;
}

/// <summary>
/// In-memory index of every file name on the chosen roots. A parallel crawl builds it, a snapshot on disk makes the
/// next launch instant, and file watchers keep it current between rescans.
/// </summary>
public sealed class FileIndex
{
    private const int FormatVersion = 2; // v2 added the system/hidden flag bits

    private Entry[] entries = Array.Empty<Entry>();
    private readonly object liveLock = new();
    private readonly List<Entry> added = new();
    private readonly HashSet<string> addedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> removedDirs = new();
    private readonly List<FileSystemWatcher> watchers = new();
    private HashSet<string> excludes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? scanCts;

    public bool Scanning { get; private set; }
    public int Count { get { lock (liveLock) return entries.Length + added.Count; } }

    /// <summary>Raised from worker threads while crawling, with the number of entries found so far.</summary>
    public event Action<long>? Progress;
    /// <summary>Raised from worker threads when a watcher adds or removes something.</summary>
    public event Action? LiveChanged;
    /// <summary>Raised when watchers lose events (buffer overflow) and a rescan is the only way to catch up.</summary>
    public event Action? NeedsRescan;

    public static string SnapshotPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverySearch", "index.bin");

    public static List<string> ResolveRoots(AppSettings settings)
    {
        var roots = settings.Roots.Where(Directory.Exists).ToList();
        if (roots.Count > 0) return roots;
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    // ---- crawling ----------------------------------------------------------------------------------------------

    public async Task ScanAsync(IReadOnlyList<string> roots, IEnumerable<string> excludeNames)
    {
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource();
        var ct = cts.Token;
        excludes = new HashSet<string>(excludeNames, StringComparer.OrdinalIgnoreCase);
        Scanning = true;
        try
        {
            var result = await Task.Run(() => Crawl(roots, ct), ct);
            ct.ThrowIfCancellationRequested();
            lock (liveLock)
            {
                entries = result;
                added.Clear();
                addedKeys.Clear();
                removed.Clear();
                removedDirs.Clear();
            }
            StartWatching(roots);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (scanCts == cts) Scanning = false;
        }
    }

    private Entry[] Crawl(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var queue = new BlockingCollection<WorkItem>(new ConcurrentQueue<WorkItem>());
        int pending = 0;
        long lastReport = 0;
        int workerCount = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
        var locals = new List<Entry>[workerCount];

        foreach (var root in roots)
        {
            Interlocked.Increment(ref pending);
            queue.Add(new WorkItem(root, false, 0));
        }
        if (pending == 0) return Array.Empty<Entry>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        var tasks = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            var mine = locals[w] = new List<Entry>(1 << 16);
            tasks[w] = Task.Factory.StartNew(() =>
            {
                try
                {
                    foreach (var work in queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            if (!ct.IsCancellationRequested) CrawlDirectory(work, options, mine, queue, ref pending);
                        }
                        catch { /* unreadable folder: skip it */ }
                        finally
                        {
                            if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
                        }
                        long now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref lastReport) > 200)
                        {
                            Interlocked.Exchange(ref lastReport, now);
                            Progress?.Invoke(locals.Sum(l => l?.Count ?? 0));
                        }
                    }
                }
                catch (InvalidOperationException) { }
            }, TaskCreationOptions.LongRunning);
        }
        Task.WaitAll(tasks);

        int count = locals.Sum(l => l.Count);
        var merged = new Entry[count];
        int at = 0;
        foreach (var l in locals)
        {
            l.CopyTo(merged, at);
            at += l.Count;
        }
        Progress?.Invoke(count);
        return merged;
    }

    private readonly record struct WorkItem(string Path, bool System, int Depth);

    // Folders whose contents are operating-system or tool noise rather than the user's own files.
    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "__pycache__", "site-packages", ".nuget", ".gradle", ".vs", ".cache",
        ".pnpm-store", ".npm", ".yarn", ".m2", ".cargo", ".rustup", ".venv", "venv",
        "AppData", "ProgramData", "$Recycle.Bin", "System Volume Information", "$WinREAgent", "WinSxS",
    };
    // Only system when they sit directly on a drive root ("D:\Docs\Windows" is the user's own folder).
    private static readonly HashSet<string> SystemRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "Recovery", "PerfLogs", "$SysReset", "MSOCache", "Intel",
    };

    private static bool IsSystemFolder(string name, int depth) =>
        SystemFolders.Contains(name) || depth == 1 && SystemRootFolders.Contains(name);

    private static bool IsSystemPath(string path)
    {
        var parts = path.Split('\\');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0 && IsSystemFolder(parts[i], i)) return true;
        return false;
    }

    private void CrawlDirectory(WorkItem item, EnumerationOptions options, List<Entry> into, BlockingCollection<WorkItem> queue, ref int pending)
    {
        string dir = item.Path;
        byte inherited = (byte)(item.System ? 8 : 0);
        var listing = new FileSystemEnumerable<Entry>(dir, (ref FileSystemEntry e) => new Entry
        {
            Name = e.FileName.ToString(),
            Dir = dir,
            Size = e.IsDirectory ? 0 : e.Length,
            Mtime = e.LastWriteTimeUtc.UtcTicks,
            // bit 0 folder, bit 1 reparse point (junction/symlink: listed but never entered),
            // bit 2 hidden/system attribute, bit 3 inside a system or build folder
            Flags = (byte)((e.IsDirectory ? 1 : 0)
                | ((e.Attributes & FileAttributes.ReparsePoint) != 0 ? 2 : 0)
                | ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ? 4 : 0)
                | inherited),
        }, options);

        foreach (var found in listing)
        {
            var entry = found;
            bool real = (entry.Flags & 3) == 1; // a real folder, not a junction
            bool childSystem = item.System;
            if (real)
            {
                if (IsSystemFolder(entry.Name, item.Depth + 1) || (entry.Flags & 4) != 0) childSystem = true;
                if (childSystem) entry.Flags |= 8;
            }
            into.Add(entry);
            if (!real) continue; // only real folders are entered
            if (excludes.Contains(entry.Name)) continue;
            string child = Path.Join(dir, entry.Name);
            if (excludes.Contains(child)) continue;
            Interlocked.Increment(ref pending);
            queue.Add(new WorkItem(child, childSystem, item.Depth + 1));
        }
    }

    // ---- searching ---------------------------------------------------------------------------------------------

    private struct Hit
    {
        public long Key;
        public int Idx;
    }

    private sealed class Local
    {
        public readonly List<Hit> Hits = new();
        public long Count;
    }

    /// <summary>Runs the query across all entries in parallel and returns the best <paramref name="limit"/> plus the total match count.</summary>
    /// <param name="bonus">Optional ranking boost per entry (e.g. files you open often); subtracted from the sort key.</param>
    public (List<Entry> Top, long Total) Search(Query q, int limit, CancellationToken ct, Func<Entry, long>? bonus = null)
    {
        // With a boost, keep a wider candidate pool so a favourite file just outside the plain top-N can still rise.
        int pool = bonus == null ? limit : Math.Max(limit * 3, 1500);
        Entry[] snap;
        Entry[] extra;
        lock (liveLock)
        {
            snap = entries;
            extra = added.ToArray();
        }

        var merged = new List<Hit>();
        long total = 0;
        var gate = new object();

        static void Trim(List<Hit> hits, int keep)
        {
            hits.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Idx.CompareTo(b.Idx));
            if (hits.Count > keep) hits.RemoveRange(keep, hits.Count - keep);
        }

        Parallel.ForEach(
            Partitioner.Create(0, snap.Length, 32768),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new Local(),
            (range, _, local) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                    if (!q.Match(in snap[i], out long key)) continue;
                    local.Count++;
                    local.Hits.Add(new Hit { Key = key, Idx = i });
                    if (local.Hits.Count > pool * 4) Trim(local.Hits, pool);
                }
                return local;
            },
            local =>
            {
                Trim(local.Hits, pool);
                lock (gate)
                {
                    total += local.Count;
                    merged.AddRange(local.Hits);
                }
            });

        for (int j = 0; j < extra.Length; j++)
        {
            if (!q.Match(in extra[j], out long key)) continue;
            total++;
            merged.Add(new Hit { Key = key, Idx = snap.Length + j });
        }

        Trim(merged, pool + 64);
        if (bonus != null)
        {
            for (int i = 0; i < merged.Count; i++)
            {
                var h = merged[i];
                var entry = h.Idx < snap.Length ? snap[h.Idx] : extra[h.Idx - snap.Length];
                h.Key -= bonus(entry);
                merged[i] = h;
            }
        }
        Trim(merged, limit + 64);
        var top = new List<Entry>(Math.Min(limit, merged.Count));
        foreach (var hit in merged)
        {
            var e = hit.Idx < snap.Length ? snap[hit.Idx] : extra[hit.Idx - snap.Length];
            if (IsRemoved(e)) { total--; continue; }
            top.Add(e);
            if (top.Count == limit) break;
        }
        return (top, total);
    }

    private bool IsRemoved(in Entry e)
    {
        lock (liveLock)
        {
            if (removed.Count == 0 && removedDirs.Count == 0) return false;
            string path = Path.Join(e.Dir, e.Name);
            if (removed.Contains(path)) return true;
            foreach (var d in removedDirs)
                if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // ---- live updates ------------------------------------------------------------------------------------------

    private void StartWatching(IReadOnlyList<string> roots)
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        foreach (var root in roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                w.Created += (_, e) => OnCreated(e.FullPath);
                w.Deleted += (_, e) => OnDeleted(e.FullPath);
                w.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
                w.Error += (_, _) => NeedsRescan?.Invoke();
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { /* a root we can't watch is still searchable, just not live */ }
        }
    }

    private bool IsExcludedPath(string path)
    {
        foreach (var part in path.Split('\\'))
            if (part.Length > 0 && excludes.Contains(part)) return true;
        return excludes.Contains(path);
    }

    private void OnCreated(string path)
    {
        try
        {
            if (IsExcludedPath(path)) return;
            bool isDir = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            FileSystemInfo info = isDir ? new DirectoryInfo(path) : new FileInfo(path);
            var entry = new Entry
            {
                Name = info.Name,
                Dir = Path.GetDirectoryName(path) ?? "",
                Size = isDir ? 0 : ((FileInfo)info).Length,
                Mtime = info.LastWriteTimeUtc.Ticks,
                Flags = (byte)((isDir ? 1 : 0)
                    | ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ? 4 : 0)
                    | (IsSystemPath(path) ? 8 : 0)),
            };
            AddLive(entry, path);
            LiveChanged?.Invoke();
            // A folder that appears with contents (moved in, copied, unzipped) never reports its children.
            if (isDir) Task.Run(() => AddSubtree(path));
        }
        catch { /* gone again already, or not accessible */ }
    }

    /// <summary>Adds a new entry unless it's already known; a re-created path restores the hidden snapshot entry instead.</summary>
    private void AddLive(Entry entry, string path)
    {
        lock (liveLock)
        {
            bool restored = removed.Remove(path);
            removedDirs.RemoveAll(d => string.Equals(d, path + "\\", StringComparison.OrdinalIgnoreCase));
            if (!restored && addedKeys.Add(path)) added.Add(entry);
        }
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return; // case-only rename: nothing moved
        OnDeleted(oldPath);
        OnCreated(newPath); // for a folder this also indexes everything now living under the new name
        if (Directory.Exists(newPath))
            lock (liveLock) removedDirs.Add(oldPath + "\\"); // the old name's contents are gone even without a delete event
    }

    private void AddSubtree(string root)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
            {
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                string full = info.FullName;
                if (IsExcludedPath(full)) continue;
                AddLive(new Entry
                {
                    Name = info.Name,
                    Dir = Path.GetDirectoryName(full) ?? "",
                    Size = isDir ? 0 : ((FileInfo)info).Length,
                    Mtime = info.LastWriteTimeUtc.Ticks,
                    Flags = (byte)((isDir ? 1 : 0)
                        | ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ? 4 : 0)
                        | (IsSystemPath(full) ? 8 : 0)),
                }, full);
            }
            LiveChanged?.Invoke();
        }
        catch { /* folder vanished while being read */ }
    }

    /// <summary>Replaces the whole index (used by tests and by anything that builds entries itself).</summary>
    public void ReplaceAll(Entry[] all)
    {
        lock (liveLock)
        {
            entries = all;
            added.Clear();
            addedKeys.Clear();
            removed.Clear();
            removedDirs.Clear();
        }
    }

    private void OnDeleted(string path)
    {
        lock (liveLock)
        {
            removed.Add(path);
            // No way to know if it was a folder; if the name has no extension, treat it as a prefix too.
            if (!Path.HasExtension(path)) removedDirs.Add(path + "\\");
        }
        LiveChanged?.Invoke();
    }

    // ---- snapshot ----------------------------------------------------------------------------------------------

    public void SaveSnapshot(IReadOnlyList<string> roots)
    {
        try
        {
            Entry[] snap;
            lock (liveLock) snap = entries;
            if (snap.Length == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            string tmp = SnapshotPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(FormatVersion);
                w.Write(string.Join('|', roots));
                var dirIds = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                var dirs = new List<string>();
                foreach (var e in snap)
                    if (dirIds.TryAdd(e.Dir, dirs.Count)) dirs.Add(e.Dir);
                w.Write(dirs.Count);
                foreach (var d in dirs) w.Write(d);
                w.Write(snap.Length);
                foreach (var e in snap)
                {
                    w.Write(e.Name);
                    w.Write(dirIds[e.Dir]);
                    w.Write(e.Size);
                    w.Write(e.Mtime);
                    w.Write(e.Flags);
                }
            }
            File.Move(tmp, SnapshotPath, true);
        }
        catch { /* the snapshot only speeds up the next launch */ }
    }

    /// <summary>Loads the last snapshot if it was taken for the same roots. Returns false when there is nothing usable.</summary>
    public bool TryLoadSnapshot(IReadOnlyList<string> roots)
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return false;
            using var fs = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != FormatVersion) return false;
            if (r.ReadString() != string.Join('|', roots)) return false;
            var dirs = new string[r.ReadInt32()];
            for (int i = 0; i < dirs.Length; i++) dirs[i] = r.ReadString();
            var loaded = new Entry[r.ReadInt32()];
            for (int i = 0; i < loaded.Length; i++)
            {
                loaded[i] = new Entry
                {
                    Name = r.ReadString(),
                    Dir = dirs[r.ReadInt32()],
                    Size = r.ReadInt64(),
                    Mtime = r.ReadInt64(),
                    Flags = r.ReadByte(),
                };
            }
            lock (liveLock) entries = loaded;
            return true;
        }
        catch { return false; }
    }
}
