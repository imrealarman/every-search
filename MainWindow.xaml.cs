using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using EverySearch.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace EverySearch;

public sealed partial class MainWindow : Window
{
    private const int MaxResults = 500;
    private const int PaletteWidth = 940, PaletteHeight = 560;
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IntPtr hwnd;
    private readonly AppSettings settings = AppSettings.Load();
    private readonly FileIndex index = new();
    private readonly NativeShell shell;
    private readonly Dictionary<TypeKind, Button> chips = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer liveTimer;

    private List<ResultItem> results = new();
    private int selected = -1;
    private CancellationTokenSource? searchCts;
    private MatchMode mode;
    private bool caseSensitive, matchPath, includeSystem;
    private TypeKind activeTypes;
    private readonly List<string> scope = new();
    private Flyout? scopeFlyout;
    private bool scopeOpen, pickerOpen;
    private bool ready, visible, quitting, capturingHotkey, liveDirty;
    private long scanned;
    private string resultInfo = "";
    private string? transient;
    private readonly UsageStore usage = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? rescanTimer;
    private string rootsKey = "";
    private bool dragging, overlayOpen, welcomePending, dialogOpen;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        Title = "EverySearch";
        hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // lives in the tray, not on the taskbar

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        BuildChips();
        LoadOptions();
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => Palette.Apply(Root.ActualTheme == ElementTheme.Light);
        Root.Loaded += (_, _) => Palette.Apply(Root.ActualTheme == ElementTheme.Light);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && visible && !capturingHotkey && !pickerOpen && !dragging) HidePalette();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (quitting) return;
            e.Cancel = true; // closing just tucks the palette away
            HidePalette();
        };

        shell = new NativeShell(hwnd);
        shell.HotkeyPressed += TogglePalette;
        shell.TrayClicked += TogglePalette;
        shell.TrayMenuChosen += id =>
        {
            if (id == NativeShell.MenuShow) ShowPalette();
            else if (id == NativeShell.MenuRescan) _ = StartScanAsync();
            else if (id == NativeShell.MenuQuit) Quit();
        };
        shell.AddTrayIcon(icon, "EverySearch");
        if (!shell.RegisterHotkey(settings.Hotkey, out var hotkeyError)) transient = hotkeyError;

        index.Progress += n =>
        {
            scanned = n;
            DispatcherQueue.TryEnqueue(UpdateStatus);
        };
        index.LiveChanged += () => liveDirty = true;
        index.NeedsRescan += () => DispatcherQueue.TryEnqueue(() => _ = StartScanAsync());

        liveTimer = DispatcherQueue.CreateTimer();
        liveTimer.Interval = TimeSpan.FromMilliseconds(700);
        liveTimer.Tick += (_, _) =>
        {
            if (!liveDirty) return;
            liveDirty = false;
            if (visible) { UpdateStatus(); ScheduleSearch(); }
        };
        liveTimer.Start();

        rescanTimer = DispatcherQueue.CreateTimer();
        rescanTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, settings.RescanMinutes));
        rescanTimer.Tick += (_, _) => _ = StartScanAsync();
        rescanTimer.Start();

        // A USB drive plugged in (or removed) changes what should be indexed.
        var driveTimer = DispatcherQueue.CreateTimer();
        driveTimer.Interval = TimeSpan.FromSeconds(20);
        driveTimer.Tick += (_, _) =>
        {
            if (!index.Scanning && string.Join('|', FileIndex.ResolveRoots(settings)) != rootsKey) _ = StartScanAsync();
        };
        driveTimer.Start();

        welcomePending = !settings.FirstRunDone;
        ready = true;
        UpdateStatus();
        ShowItems(new List<ResultItem>());

        // Use last session's snapshot straight away, then refresh it in the background.
        _ = Task.Run(() => index.TryLoadSnapshot(FileIndex.ResolveRoots(settings)))
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStatus();
                ScheduleSearch();
                _ = StartScanAsync();
            }));

        if (!startHidden) ShowPalette();
    }

    // ---- show / hide -------------------------------------------------------------------------------------------

    public void ShowPalette()
    {
        PlaceOnCursorMonitor();
        visible = true;
        AppWindow.Show();
        Activate();
        NativeShell.BringToFront(hwnd);
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
        UpdateStatus();
        if (welcomePending) ShowWelcome();
    }

    private void HidePalette()
    {
        if (!visible) return;
        visible = false;
        HidePreview();
        SaveOptions();
        AppWindow.Hide();
    }

    private void TogglePalette()
    {
        if (visible) HidePalette(); else ShowPalette();
    }

    private void PlaceOnCursorMonitor()
    {
        var (cx, cy) = NativeShell.CursorPosition();
        var work = DisplayArea.GetFromPoint(new PointInt32(cx, cy), DisplayAreaFallback.Nearest).WorkArea;
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int w = Math.Min((int)(PaletteWidth * scale), work.Width);
        int h = Math.Min((int)(PaletteHeight * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (int)(work.Height * 0.14), w, h));
    }

    private void Quit()
    {
        quitting = true;
        SaveOptions();
        shell.Dispose();
        Application.Current.Exit();
    }

    // ---- options & chips ---------------------------------------------------------------------------------------

    private void BuildChips()
    {
        int n = 1;
        foreach (var (kind, label, _) in FileTypes.All)
        {
            var chip = new Button { Content = label, Tag = kind, Style = (Style)Application.Current.Resources["ChipButton"] };
            ToolTipService.SetToolTip(chip, $"Ctrl+{n++}");
            chip.Click += (_, _) => ToggleType(kind);
            chips[kind] = chip;
            ChipRow.Children.Add(chip);
        }
    }

    private void LoadOptions()
    {
        mode = Enum.TryParse<MatchMode>(settings.Mode, out var m) ? m : MatchMode.Contains;
        caseSensitive = settings.CaseSensitive;
        matchPath = settings.MatchPath;
        includeSystem = settings.IncludeSystem;
        foreach (var t in settings.Types)
            if (Enum.TryParse<TypeKind>(t, out var k)) activeTypes |= k;
        ExtBox.Text = settings.Extensions;
        scope.AddRange(settings.Scope);
        RefreshOptionStyles();
    }

    private void SaveOptions()
    {
        settings.Mode = mode.ToString();
        settings.CaseSensitive = caseSensitive;
        settings.MatchPath = matchPath;
        settings.IncludeSystem = includeSystem;
        settings.Extensions = ExtBox.Text;
        settings.Scope = scope.ToList();
        settings.Types = FileTypes.All.Where(t => (activeTypes & t.Kind) != 0).Select(t => t.Kind.ToString()).ToList();
        settings.Save();
    }

    private static Style StyleOf(bool on) => (Style)Application.Current.Resources[on ? "ChipButtonOn" : "ChipButton"];

    private void RefreshOptionStyles()
    {
        foreach (var (kind, chip) in chips) chip.Style = StyleOf((activeTypes & kind) != 0);
        PathButton.Style = StyleOf(matchPath);
        CaseButton.Style = StyleOf(caseSensitive);
        SystemButton.Style = StyleOf(includeSystem);
        ModeButton.Style = StyleOf(mode != MatchMode.Contains);
        ModeButton.Content = mode.ToString();
        ScopeButton.Style = StyleOf(scope.Count > 0);
        ScopeButton.Content = scope.Count switch
        {
            0 => "Entire PC",
            1 => ScopeLabel(scope[0]),
            _ => $"{scope.Count} places",
        };
        ToolTipService.SetToolTip(ScopeButton, scope.Count == 0 ? "Searching the entire PC (Ctrl+D to change)" : string.Join("\n", scope));
    }

    private static string ScopeLabel(string path)
    {
        string trimmed = path.TrimEnd('\\');
        if (trimmed.Length <= 2) return trimmed + "\\"; // a drive: "D:\"
        string leaf = Path.GetFileName(trimmed);
        return leaf.Length > 0 ? leaf : trimmed;
    }

    // ---- search scope (entire PC / drives / folders) -----------------------------------------------------------

    private void ScopeButton_Click(object sender, RoutedEventArgs e) => OpenScopeFlyout();

    private void OpenScopeFlyout()
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 280 };
        FillScopePanel(panel);
        scopeFlyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        scopeFlyout.Opened += (_, _) => scopeOpen = true;
        scopeFlyout.Closed += (_, _) =>
        {
            scopeOpen = false;
            QueryBox.Focus(FocusState.Programmatic);
        };
        scopeFlyout.ShowAt(ScopeButton);
    }

    /// <summary>Entire-PC checkbox, one checkbox per drive (tick several), any chosen folders, and "Add folder…".</summary>
    private void FillScopePanel(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "SEARCH IN", Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(0, 0, 0, 4) });

        var entire = new CheckBox { Content = "Entire PC", IsChecked = scope.Count == 0 };
        entire.Click += (_, _) => { scope.Clear(); ScopeChanged(panel); };
        panel.Children.Add(entire);

        var drivePaths = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string path = drive.Name.TrimEnd('\\');
            drivePaths.Add(path);
            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? path : $"{path}  {drive.VolumeLabel}";
            AddScopeBox(panel, label, path);
        }
        foreach (var folder in scope.Where(s => !drivePaths.Contains(s, StringComparer.OrdinalIgnoreCase)))
            AddScopeBox(panel, folder, folder);

        var ghost = (Style)Application.Current.Resources["GhostButton"];
        if (settings.SavedScopes.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "SAVED SCOPES", Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(0, 10, 0, 2) });
            foreach (var (name, paths) in settings.SavedScopes.ToList())
            {
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var apply = new Button
                {
                    Content = $"{name}  ({paths.Count})", Style = ghost,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                apply.Click += (_, _) => { scope.Clear(); scope.AddRange(paths); ScopeChanged(panel); };
                var remove = new Button { Content = "✕", Style = ghost };
                remove.Click += (_, _) => { settings.SavedScopes.Remove(name); settings.Save(); FillScopePanel(panel); };
                Grid.SetColumn(remove, 1);
                row.Children.Add(apply);
                row.Children.Add(remove);
                panel.Children.Add(row);
            }
        }
        if (scope.Count > 0)
        {
            var saveRow = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = new TextBox { PlaceholderText = "Save this scope as…" };
            var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["SecondaryButton"] };
            save.Click += (_, _) =>
            {
                string name = nameBox.Text.Trim();
                if (name.Length == 0) return;
                settings.SavedScopes[name] = scope.ToList();
                settings.Save();
                FillScopePanel(panel);
            };
            Grid.SetColumn(save, 1);
            saveRow.Children.Add(nameBox);
            saveRow.Children.Add(save);
            panel.Children.Add(saveRow);
        }

        var add = new Button { Content = "Add folder…", Style = (Style)Application.Current.Resources["SecondaryButton"], Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += async (_, _) => await AddScopeFolderAsync(panel);
        panel.Children.Add(add);
    }

    private void AddScopeBox(StackPanel panel, string label, string path)
    {
        bool on = scope.Contains(path, StringComparer.OrdinalIgnoreCase);
        var box = new CheckBox { Content = label, IsChecked = on };
        box.Click += (_, _) =>
        {
            scope.RemoveAll(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase));
            if (box.IsChecked == true) scope.Add(path);
            ScopeChanged(panel);
        };
        panel.Children.Add(box);
    }

    private void ScopeChanged(StackPanel panel)
    {
        RefreshOptionStyles();
        FillScopePanel(panel);
        ScheduleSearch();
    }

    private async Task AddScopeFolderAsync(StackPanel panel)
    {
        pickerOpen = true; // the folder dialog takes focus; don't treat that as "clicked away"
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !scope.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            {
                scope.Add(folder.Path);
                ScopeChanged(panel);
            }
        }
        catch (Exception ex) { transient = "Couldn't open the folder picker: " + ex.Message; UpdateStatus(); }
        finally
        {
            pickerOpen = false;
            ShowPalette();
        }
    }

    private void ToggleType(TypeKind kind)
    {
        activeTypes ^= kind;
        RefreshOptionStyles();
        ScheduleSearch();
    }

    private void ClearFilters()
    {
        activeTypes = TypeKind.None;
        ExtBox.Text = "";
        scope.Clear();
        mode = MatchMode.Contains;
        caseSensitive = matchPath = includeSystem = false;
        RefreshOptionStyles();
        ScheduleSearch();
    }

    private void CycleMode()
    {
        mode = (MatchMode)(((int)mode + 1) % Enum.GetValues<MatchMode>().Length);
        RefreshOptionStyles();
        ScheduleSearch();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = settings.Theme switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        Palette.Apply(Root.ActualTheme == ElementTheme.Light);
    }

    private void PathButton_Click(object sender, RoutedEventArgs e) { matchPath = !matchPath; RefreshOptionStyles(); ScheduleSearch(); QueryBox.Focus(FocusState.Programmatic); }
    private void SystemButton_Click(object sender, RoutedEventArgs e) { ToggleSystem(); QueryBox.Focus(FocusState.Programmatic); }

    private void ToggleSystem()
    {
        includeSystem = !includeSystem;
        RefreshOptionStyles();
        ScheduleSearch();
    }

    private void CaseButton_Click(object sender, RoutedEventArgs e) { caseSensitive = !caseSensitive; RefreshOptionStyles(); ScheduleSearch(); QueryBox.Focus(FocusState.Programmatic); }
    private void ModeButton_Click(object sender, RoutedEventArgs e) { CycleMode(); QueryBox.Focus(FocusState.Programmatic); }
    private void ExtBox_TextChanged(object sender, TextChangedEventArgs e) { if (ready) ScheduleSearch(); }
    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) { if (ready) ScheduleSearch(); }

    // ---- searching ---------------------------------------------------------------------------------------------

    private void ScheduleSearch()
    {
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var token = cts.Token;
        string text = QueryBox.Text;
        transient = null;

        if (text.StartsWith('>'))
        {
            ShowCommands(text[1..].Trim());
            return;
        }

        var query = Query.Parse(text, mode, caseSensitive, matchPath, activeTypes, ExtBox.Text, scope, includeSystem);
        if (query.Error != null)
        {
            resultInfo = query.Error;
            ShowItems(new List<ResultItem>());
            UpdateStatus();
            return;
        }
        if (!query.HasText && !query.HasFilter)
        {
            resultInfo = "";
            ShowItems(new List<ResultItem>());
            UpdateStatus();
            return;
        }

        if (query.ContentText != null)
        {
            _ = RunContentSearchAsync(query, token);
            return;
        }

        var terms = query.HighlightTerms;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(25, token); // collapses fast typing into one search
                var clock = Stopwatch.StartNew();
                // Files you open often get a boost, so they rise above equally good matches.
                var (top, total) = index.Search(query, MaxResults, token, e => usage.Bonus(Path.Join(e.Dir, e.Name)));
                long ms = clock.ElapsedMilliseconds;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    ShowItems(top.Select(e => ToItem(e, null, terms)).ToList());
                    resultInfo = total > top.Count
                        ? $"{total:N0} results (showing {top.Count:N0}) · {ms} ms"
                        : $"{total:N0} results · {ms} ms";
                    UpdateStatus();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => { resultInfo = "Search failed: " + ex.Message; UpdateStatus(); });
            }
        }, token);
    }

    /// <summary>Searches inside files (content: operator). Waits for typing to settle, then reads candidate files in parallel.</summary>
    private async Task RunContentSearchAsync(Query query, CancellationToken token)
    {
        string needle = query.ContentText!;
        try
        {
            await Task.Delay(300, token);
            resultInfo = "Searching inside files…";
            UpdateStatus();
            var clock = Stopwatch.StartNew();
            var (candidates, _) = await Task.Run(() => index.Search(query, 20000, token), token);
            var progress = new Progress<int>(n =>
            {
                if (token.IsCancellationRequested) return;
                resultInfo = $"Searching inside files… {n:N0} read";
                UpdateStatus();
            });
            var found = await ContentSearch.SearchAsync(candidates, needle, caseSensitive, MaxResults, TimeSpan.FromSeconds(20), progress, token);
            if (token.IsCancellationRequested) return;
            ShowItems(found.Hits.Select(h => ToItem(h.Entry, h.Snippet, query.HighlightTerms)).ToList());
            resultInfo = $"{found.Hits.Count:N0} files contain \"{needle}\" · read {found.Searched:N0} of {found.Candidates:N0} · {clock.Elapsed.TotalSeconds:0.0}s"
                + (found.Truncated ? " · partial: narrow the scope (Ctrl+D) or type" : "");
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            resultInfo = "Content search failed: " + ex.Message;
            UpdateStatus();
        }
    }

    /// <param name="snippet">For content hits: the matching text, shown in place of the folder path.</param>
    private static ResultItem ToItem(Entry e, string? snippet = null, string[]? terms = null)
    {
        bool dir = (e.Flags & 1) != 0;
        var kind = FileTypes.KindOfEntry(e.Name, dir);
        return new ResultItem
        {
            Name = e.Name,
            Dir = snippet ?? e.Dir,
            FullPath = Path.Join(e.Dir, e.Name),
            Icon = IconFor(kind),
            TypeLabel = FileTypes.Label(kind),
            Terms = terms ?? Array.Empty<string>(),
            IsDir = dir,
            SizeText = dir ? "" : ResultItem.FormatSize(e.Size),
            DateText = e.Mtime > 0 ? new DateTime(e.Mtime, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy") : "",
        };
    }

    private void ShowItems(List<ResultItem> items)
    {
        hovered = null;
        HidePreview();
        results = items;
        ResultList.ItemsSource = items;
        selected = items.Count > 0 ? 0 : -1;
        if (selected >= 0)
        {
            items[0].IsSelected = true;
            ResultList.ScrollIntoView(items[0]);
        }

        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool searching = QueryBox.Text.Length > 0 || activeTypes != TypeKind.None || ExtBox.Text.Length > 0 || scope.Count > 0;
        EmptyTitle.Text = searching ? "No matches" : "Type to search this whole PC";
        EmptyHint.Text = searching
            ? "Try another pattern mode (Ctrl+M) or loosen the type filters"
            : "ext:pdf   type:image,video   size:>10mb   in:D:\\Projects   folder:\nPick types above (Ctrl+1…9) or a scope (Ctrl+D)";
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        string first = transient ?? resultInfo;
        if (first.Length > 0) parts.Add(first);
        parts.Add(index.Scanning ? $"indexing… {scanned:N0} found" : $"{index.Count:N0} indexed");
        StatusText.Text = string.Join("   ·   ", parts);
    }

    private async Task StartScanAsync()
    {
        var roots = FileIndex.ResolveRoots(settings);
        rootsKey = string.Join('|', roots);
        scanned = 0;
        UpdateStatus();
        await index.ScanAsync(roots, settings.Excludes);
        UpdateStatus();
        ScheduleSearch();
        _ = Task.Run(() => index.SaveSnapshot(roots));
    }

    // ---- results: selection and actions ------------------------------------------------------------------------

    private void MoveSelection(int delta)
    {
        if (results.Count == 0) return;
        int next = Math.Clamp((selected < 0 ? 0 : selected) + delta, 0, results.Count - 1);
        if (selected >= 0) results[selected].IsSelected = false;
        selected = next;
        results[next].IsSelected = true;
        ResultList.ScrollIntoView(results[next]);
        if (IsDown(VirtualKey.Control)) _ = ShowPreviewAsync(results[next]); // Ctrl + arrow keys previews the selection too
    }

    private ResultItem? SelectedResult => selected >= 0 && selected < results.Count ? results[selected] : null;

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ResultItem item) return;
        if (selected >= 0) results[selected].IsSelected = false;
        selected = results.IndexOf(item);
        item.IsSelected = true;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Activate(reveal: false, admin: false);

    private void Activate(bool reveal, bool admin)
    {
        var item = SelectedResult;
        if (item == null) return;
        if (item.Command != null)
        {
            item.Command();
            if (!capturingHotkey && !quitting) QueryBox.Text = "";
            return;
        }
        try
        {
            if (reveal)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true, Verb = admin ? "runas" : "" });
        }
        catch (Exception ex)
        {
            transient = "Couldn't open: " + ex.Message;
            UpdateStatus();
            return;
        }
        usage.Record(item.FullPath);
        HidePalette();
    }

    private void OpenTerminal()
    {
        var item = SelectedResult;
        if (item == null || item.Command != null) return;
        string dir = item.IsDir ? item.FullPath : Path.GetDirectoryName(item.FullPath) ?? item.FullPath;
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\"") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true }); }
            catch (Exception ex) { transient = "Couldn't open a terminal: " + ex.Message; UpdateStatus(); return; }
        }
        HidePalette();
    }

    private void CopyPath(bool nameOnly)
    {
        var item = SelectedResult;
        if (item == null || item.Command != null) return;
        var package = new DataPackage();
        package.SetText(nameOnly ? item.Name : item.FullPath);
        Clipboard.SetContent(package);
        transient = nameOnly ? "Copied name" : "Copied path";
        UpdateStatus();
    }

    // ---- keyboard ----------------------------------------------------------------------------------------------

    // ---- type icons --------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, BitmapImage> IconCache = new();

    private static BitmapImage IconFor(TypeKind kind)
    {
        string file = FileTypes.IconFile(kind);
        if (!IconCache.TryGetValue(file, out var icon))
        {
            icon = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Types", file))) { DecodePixelWidth = 64 };
            IconCache[file] = icon;
        }
        return icon;
    }

    // ---- hold Ctrl + hover: preview ----------------------------------------------------------------------------

    private static readonly HashSet<string> PlainTextExts = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "md", "log", "csv", "tsv", "ini", "cfg", "conf", "json", "xml", "yml", "yaml", "toml", "srt" };
    private static readonly HashSet<string> NativeImageExts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "bmp", "ico", "tif", "tiff" };

    private ResultItem? hovered, previewItem;
    private CancellationTokenSource? previewCts;

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ResultItem item) return;
        hovered = item;
        if (IsDown(VirtualKey.Control)) _ = ShowPreviewAsync(item);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ResultItem item && ReferenceEquals(hovered, item))
        {
            hovered = null;
            HidePreview();
        }
    }

    private void HidePreview()
    {
        previewCts?.Cancel();
        previewItem = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
    }

    private async Task ShowPreviewAsync(ResultItem item)
    {
        if (item.Command != null || ReferenceEquals(previewItem, item)) return;
        previewCts?.Cancel();
        var cts = previewCts = new CancellationTokenSource();
        var ct = cts.Token;
        previewItem = item;

        PreviewTitle.Text = item.Name;
        PreviewMeta.Text = string.Join(" · ", new[] { item.TypeLabel, item.SizeText, item.DateText }.Where(s => s.Length > 0));
        PreviewImage.Source = null;
        PreviewImage.Visibility = PreviewText.Visibility = PreviewNone.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;

        try
        {
            string ext = FileTypes.ExtensionOf(item.Name);
            var kind = FileTypes.KindOfEntry(item.Name, item.IsDir);
            string? text = null, extra = null;
            ImageSource? image = null;

            if (item.IsDir) text = await Task.Run(() => ListFolder(item.FullPath), ct);
            else if (PlainTextExts.Contains(ext) || kind == TypeKind.Code) text = await Task.Run(() => ReadHead(item.FullPath), ct);
            else if (ext == "pdf") (image, extra) = await RenderPdfAsync(item.FullPath);
            else
            {
                image = await LoadImageAsync(item.FullPath, NativeImageExts.Contains(ext));
                extra = await DescribeMediaAsync(item.FullPath, kind);
            }

            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(extra)) PreviewMeta.Text += " · " + extra;
            if (text != null)
            {
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
            }
            else if (image != null)
            {
                PreviewImage.Source = image;
                PreviewImage.Visibility = Visibility.Visible;
            }
            else PreviewNone.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Locked, unreadable or unsupported: say so instead of showing a stale preview.
            if (!ct.IsCancellationRequested) PreviewNone.Visibility = Visibility.Visible;
        }
    }

    private static string ListFolder(string path)
    {
        var names = new List<string>();
        foreach (var p in Directory.EnumerateFileSystemEntries(path))
        {
            names.Add((Directory.Exists(p) ? "▸ " : "   ") + Path.GetFileName(p));
            if (names.Count == 20) { names.Add("…"); break; }
        }
        return names.Count == 0 ? "(empty folder)" : string.Join("\n", names);
    }

    /// <summary>First lines of a text file, or null when it looks binary.</summary>
    private static string? ReadHead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[16 * 1024];
        int n = fs.Read(buf, 0, buf.Length);
        if (n == 0) return "(empty file)";
        if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return null;
        var lines = System.Text.Encoding.UTF8.GetString(buf, 0, n).Split('\n').Take(20).Select(l =>
        {
            l = l.TrimEnd('\r').Replace("\t", "    ");
            return l.Length > 110 ? l[..110] + "…" : l;
        });
        return string.Join("\n", lines);
    }

    private static async Task<(ImageSource? Image, string? Extra)> RenderPdfAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        if (pdf.PageCount == 0) return (null, null);
        using var page = pdf.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 640 });
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        return (bmp, pdf.PageCount == 1 ? "1 page" : $"{pdf.PageCount} pages");
    }

    /// <summary>Dimensions for images; duration, size and artist/title for video and audio.</summary>
    private static async Task<string?> DescribeMediaAsync(string path, TypeKind kind)
    {
        try
        {
            if (kind is not (TypeKind.Images or TypeKind.Video or TypeKind.Audio)) return null;
            var file = await StorageFile.GetFileFromPathAsync(path);
            var parts = new List<string>();
            switch (kind)
            {
                case TypeKind.Images:
                    var img = await file.Properties.GetImagePropertiesAsync();
                    if (img.Width > 0) parts.Add($"{img.Width}×{img.Height}px");
                    break;
                case TypeKind.Video:
                    var video = await file.Properties.GetVideoPropertiesAsync();
                    parts.Add(FormatDuration(video.Duration));
                    if (video.Width > 0) parts.Add($"{video.Width}×{video.Height}");
                    break;
                case TypeKind.Audio:
                    var music = await file.Properties.GetMusicPropertiesAsync();
                    parts.Add(FormatDuration(music.Duration));
                    parts.Add(music.Artist);
                    parts.Add(music.Title);
                    break;
            }
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        catch { return null; }
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? "" : t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Common image formats load directly; everything else (video, Office, CAD, RAW...) uses the Windows shell thumbnail if one exists.</summary>
    private static async Task<ImageSource?> LoadImageAsync(string path, bool native)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        if (native)
        {
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage { DecodePixelWidth = 720 };
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 640);
        if (thumb == null || thumb.Type != ThumbnailType.Image) return null; // no real thumbnail, only a generic icon
        var thumbBmp = new BitmapImage();
        await thumbBmp.SetSourceAsync(thumb);
        return thumbBmp;
    }

    // ---- right-click menu, drag out ----------------------------------------------------------------------------

    private void SelectItem(ResultItem item)
    {
        if (selected >= 0 && selected < results.Count) results[selected].IsSelected = false;
        selected = results.IndexOf(item);
        item.IsSelected = true;
    }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ResultItem item || item.Command != null) return;
        SelectItem(item);

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action run)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            entry.Click += (_, _) => run();
            menu.Items.Add(entry);
        }
        Add("Open", "", () => Activate(reveal: false, admin: false));
        Add("Open as administrator", "", () => Activate(reveal: false, admin: true));
        Add("Show in folder", "", () => Activate(reveal: true, admin: false));
        Add("Open terminal here", "", OpenTerminal);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy path", "", () => CopyPath(nameOnly: false));
        Add("Copy name", "", () => CopyPath(nameOnly: true));
        Add("Copy file", "", () => _ = CopyFileAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Rename…", "", () => _ = RenameAsync(item));
        Add("Delete (Recycle Bin)", "", () => _ = DeleteAsync(item));
        Add("Properties", "", () => ShowProperties(item));

        menu.Opened += (_, _) => scopeOpen = true; // keep Esc/Enter for the menu
        menu.Closed += (_, _) =>
        {
            if (dialogOpen) return; // a Rename/Delete dialog opened from the menu still owns the keyboard
            scopeOpen = false;
            QueryBox.Focus(FocusState.Programmatic);
        };
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        e.Handled = true;
    }

    private async Task CopyFileAsync(ResultItem item)
    {
        try
        {
            IStorageItem stored = item.IsDir
                ? await StorageFolder.GetFolderFromPathAsync(item.FullPath)
                : await StorageFile.GetFileFromPathAsync(item.FullPath);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new[] { stored });
            Clipboard.SetContent(package);
            transient = "Copied file: paste it in Explorer or an email";
        }
        catch (Exception ex) { transient = "Couldn't copy the file: " + ex.Message; }
        UpdateStatus();
    }

    private async Task<bool> ConfirmAsync(string title, object content, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        scopeOpen = dialogOpen = true; // the dialog owns the keyboard
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally
        {
            dialogOpen = scopeOpen = false;
            QueryBox.Focus(FocusState.Programmatic);
        }
    }

    private async Task RenameAsync(ResultItem item)
    {
        var box = new TextBox { Text = item.Name, MinWidth = 320 };
        if (!await ConfirmAsync("Rename", box, "Rename")) return;
        string newName = box.Text.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        try
        {
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("that name has characters Windows doesn't allow");
            string target = Path.Join(Path.GetDirectoryName(item.FullPath), newName);
            if (item.IsDir) Directory.Move(item.FullPath, target); else File.Move(item.FullPath, target);
            transient = $"Renamed to {newName}";
        }
        catch (Exception ex) { transient = "Couldn't rename: " + ex.Message; }
        UpdateStatus();
    }

    private async Task DeleteAsync(ResultItem item)
    {
        var text = new TextBlock { Text = $"Move \"{item.Name}\" to the Recycle Bin?", TextWrapping = TextWrapping.Wrap };
        if (!await ConfirmAsync("Delete", text, "Delete")) return;
        try
        {
            if (item.IsDir)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            ShowItems(results.Where(r => !ReferenceEquals(r, item)).ToList());
            transient = $"Moved {item.Name} to the Recycle Bin";
        }
        catch (Exception ex) { transient = "Couldn't delete: " + ex.Message; }
        UpdateStatus();
    }

    private void ShowProperties(ResultItem item)
    {
        try { Process.Start(new ProcessStartInfo(item.FullPath) { Verb = "properties", UseShellExecute = true }); }
        catch (Exception ex) { transient = "Couldn't show properties: " + ex.Message; UpdateStatus(); }
    }

    private void ResultList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<ResultItem>().Where(i => i.Command == null).Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        dragging = true; // dragging into another app deactivates this window; don't hide it mid-drag
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetText(string.Join("\r\n", paths));
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var p in paths)
                {
                    try
                    {
                        items.Add(Directory.Exists(p)
                            ? await StorageFolder.GetFolderFromPathAsync(p)
                            : await StorageFile.GetFileFromPathAsync(p));
                    }
                    catch { /* vanished since the search */ }
                }
                request.SetData(items);
            }
            finally { deferral.Complete(); }
        });
    }

    private void ResultList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        dragging = false;
        HidePalette();
    }

    // ---- welcome and settings ----------------------------------------------------------------------------------

    private void OpenOverlay(UIElement panel)
    {
        WelcomePanel.Visibility = SettingsPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        overlayOpen = true;
        HidePreview();
    }

    private void CloseOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        overlayOpen = false;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void ShowWelcome()
    {
        welcomePending = false;
        WelcomeIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));
        WelcomeHotkey.Text = $"Press {settings.Hotkey} anywhere to open this window. Esc closes it; EverySearch keeps running in the tray.";
        OpenOverlay(WelcomePanel);
    }

    private void WelcomeDone_Click(object sender, RoutedEventArgs e)
    {
        settings.FirstRunDone = true;
        settings.Save();
        CloseOverlay();
    }

    private void OpenSettingsPanel()
    {
        SettingsHotkey.Text = settings.Hotkey;
        ThemeBox.SelectedIndex = settings.Theme switch { "dark" => 1, "light" => 2, _ => 0 };
        AutostartSwitch.IsOn = IsAutostart();
        RescanBox.Text = settings.RescanMinutes.ToString();
        RootsBox.Text = string.Join("\r\n", settings.Roots);
        ExcludesBox.Text = string.Join("\r\n", settings.Excludes);
        QueryBox.Text = "";
        OpenOverlay(SettingsPanel);
    }

    private static List<string> Lines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        settings.Theme = ThemeBox.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" };
        if (int.TryParse(RescanBox.Text, out int minutes) && minutes >= 5)
        {
            settings.RescanMinutes = minutes;
            if (rescanTimer != null) rescanTimer.Interval = TimeSpan.FromMinutes(minutes);
        }

        var roots = Lines(RootsBox.Text);
        var excludes = Lines(ExcludesBox.Text);
        bool indexChanged = !roots.SequenceEqual(settings.Roots, StringComparer.OrdinalIgnoreCase)
            || !excludes.SequenceEqual(settings.Excludes, StringComparer.OrdinalIgnoreCase);
        settings.Roots = roots;
        settings.Excludes = excludes;

        if (IsAutostart() != AutostartSwitch.IsOn) ToggleAutostart();
        ApplyTheme();
        settings.Save();
        CloseOverlay();
        transient = indexChanged ? "Settings saved: re-indexing" : "Settings saved";
        UpdateStatus();
        if (indexChanged) _ = StartScanAsync();
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void SettingsHotkey_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay();
        BeginHotkeyCapture();
    }

    private void SettingsForget_Click(object sender, RoutedEventArgs e)
    {
        usage.Clear();
        transient = "Forgot which files you opened";
        UpdateStatus();
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl) HidePreview();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift), alt = IsDown(VirtualKey.Menu);

        if (capturingHotkey)
        {
            e.Handled = true;
            CaptureHotkey(e.Key, ctrl, alt, shift);
            return;
        }

        if (overlayOpen)
        {
            // Welcome / settings are showing: they own the keyboard; Esc (or Enter on the welcome) dismisses.
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Enter && WelcomePanel.Visibility == Visibility.Visible)
            {
                if (WelcomePanel.Visibility == Visibility.Visible) { settings.FirstRunDone = true; settings.Save(); }
                CloseOverlay();
                e.Handled = true;
            }
            return;
        }

        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            if (hovered != null) _ = ShowPreviewAsync(hovered); // Ctrl pressed while already over a row
            return;
        }

        if (scopeOpen) return; // the scope flyout handles its own keys (Esc closes it)

        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (QueryBox.Text.Length > 0) QueryBox.Text = ""; else HidePalette();
                e.Handled = true;
                return;
            case VirtualKey.Down: MoveSelection(1); e.Handled = true; return;
            case VirtualKey.Up: MoveSelection(-1); e.Handled = true; return;
            case VirtualKey.PageDown: MoveSelection(8); e.Handled = true; return;
            case VirtualKey.PageUp: MoveSelection(-8); e.Handled = true; return;
            case VirtualKey.Enter:
                Activate(reveal: ctrl, admin: shift);
                e.Handled = true;
                return;
            case VirtualKey.F5:
                _ = StartScanAsync();
                e.Handled = true;
                return;
        }

        if (!ctrl) return;
        switch (e.Key)
        {
            case VirtualKey.M: CycleMode(); break;
            case VirtualKey.D: OpenScopeFlyout(); break;
            case VirtualKey.S: ToggleSystem(); break;
            case VirtualKey.P: matchPath = !matchPath; RefreshOptionStyles(); ScheduleSearch(); break;
            case VirtualKey.I: caseSensitive = !caseSensitive; RefreshOptionStyles(); ScheduleSearch(); break;
            case VirtualKey.E: ExtBox.Focus(FocusState.Programmatic); ExtBox.SelectAll(); break;
            case VirtualKey.L: QueryBox.Focus(FocusState.Programmatic); QueryBox.SelectAll(); break;
            case VirtualKey.T: OpenTerminal(); break;
            case VirtualKey.R: _ = StartScanAsync(); break;
            case VirtualKey.Number0: ClearFilters(); break;
            case VirtualKey.C:
                // Plain Ctrl+C keeps copying selected text from the box; with nothing selected it copies the path.
                if (shift || QueryBox.SelectionLength == 0 && !ExtBox.FocusState.HasFlag(FocusState.Keyboard) || QueryBox.SelectionLength == 0)
                {
                    CopyPath(nameOnly: shift);
                    break;
                }
                return;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9:
                int slot = e.Key - VirtualKey.Number1;
                if (slot < FileTypes.All.Length) ToggleType(FileTypes.All[slot].Kind);
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- palette commands (type ">") ---------------------------------------------------------------------------

    private ResultItem Cmd(string title, string description, string glyph, Action run) =>
        new() { Name = title, Dir = description, Glyph = glyph, Command = run };

    private void ShowCommands(string filter)
    {
        var all = new List<ResultItem>
        {
            Cmd("Choose search scope…", "Entire PC, one or more drives, or specific folders (Ctrl+D)", "", OpenScopeFlyout),
            Cmd("Rescan index", "Crawl every drive again (F5)", "", () => _ = StartScanAsync()),
            Cmd("Clear filters", "Reset types, extensions and match options (Ctrl+0)", "", ClearFilters),
            Cmd($"Theme: {settings.Theme}", "Cycle system / dark / light", "", CycleTheme),
            Cmd($"Change hotkey ({settings.Hotkey})", "Then press the shortcut you want", "", BeginHotkeyCapture),
            Cmd(IsAutostart() ? "Don't start with Windows" : "Start with Windows", "Run in the tray at sign-in", "", ToggleAutostart),
            Cmd("Settings…", "Shortcut, theme, startup, what gets indexed", "", OpenSettingsPanel),
            Cmd("Open settings file", AppSettings.FilePath, "", OpenSettings),
            Cmd("Quit EverySearch", "Stop the background app and remove the tray icon", "", Quit),
        };
        var shown = all.Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        ShowItems(shown);
        resultInfo = "Commands";
        UpdateStatus();
    }

    private void CycleTheme()
    {
        settings.Theme = settings.Theme switch { "system" => "dark", "dark" => "light", _ => "system" };
        ApplyTheme();
        transient = $"Theme: {settings.Theme}";
        UpdateStatus();
    }

    private void BeginHotkeyCapture()
    {
        capturingHotkey = true;
        QueryBox.Text = "";
        transient = "Press the new shortcut (e.g. Ctrl+Alt+Space). Esc cancels.";
        UpdateStatus();
    }

    private void CaptureHotkey(VirtualKey key, bool ctrl, bool alt, bool shift)
    {
        if (key == VirtualKey.Escape)
        {
            capturingHotkey = false;
            transient = "Hotkey unchanged";
            UpdateStatus();
            return;
        }
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftShift or VirtualKey.RightShift)
            return; // wait for the actual key
        bool win = IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);
        if (!(ctrl || alt || shift || win))
        {
            transient = "Include Ctrl, Alt, Shift or Win";
            UpdateStatus();
            return;
        }
        string spec = NativeShell.FormatHotkey(ctrl, alt, shift, win, key);
        string previous = settings.Hotkey;
        if (shell.RegisterHotkey(spec, out var error))
        {
            settings.Hotkey = spec;
            settings.Save();
            transient = $"Hotkey is now {spec}";
        }
        else
        {
            shell.RegisterHotkey(previous, out _);
            transient = error;
        }
        capturingHotkey = false;
        UpdateStatus();
    }

    private static bool IsAutostart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutostartKey);
        return key?.GetValue("EverySearch") != null;
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutostartKey, writable: true)!;
            if (IsAutostart())
            {
                key.DeleteValue("EverySearch", false);
                transient = "Won't start with Windows";
            }
            else
            {
                key.SetValue("EverySearch", $"\"{Environment.ProcessPath}\" --background");
                transient = "Will start with Windows";
            }
        }
        catch (Exception ex) { transient = "Couldn't change startup: " + ex.Message; }
        UpdateStatus();
    }

    private void OpenSettings()
    {
        settings.Save(); // makes sure the file exists
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { transient = "Couldn't open settings: " + ex.Message; UpdateStatus(); return; }
        HidePalette();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
}
