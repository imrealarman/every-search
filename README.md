# EverySearch

An instant file and folder search for Windows. EverySearch keeps an in-memory index of every file name on your PC, so results appear as you type. It lives in the system tray and opens with a global hotkey.

Built with WinUI 3 (Windows App SDK) on .NET 8.

## Features

### Search
- Searches every file and folder name on the whole PC, or on the drives and folders you choose.
- Match modes: contains, exact, wildcard, regex and fuzzy.
- Inline operators: `ext:pdf`, `type:image,video`, `size:>10mb`, `path:docs`, `folder:`, and `modified:` for date ranges.
- Filter bar with type chips (documents, images, video, audio, archives, code, CAD, apps and more), match case, include folders, and an extension filter.
- Results are ranked: the things people work with (documents, media, CAD, folders) come before system and build files, and files you open often rank higher.
- Search inside files for text. It reads plain-text and code files, and the text of docx, pptx and xlsx, scanning on demand with nothing extra indexed.
- Preview pane for the selected result.
- Command palette: start with `>` (for example `>settings`).

### Index
- A parallel crawl builds the index. A snapshot on disk makes the next launch instant, and file watchers keep it current between rescans.
- Choose the folders to index (empty means every fixed drive), list folders that are never indexed, and set the rescan interval.
- Hidden and system files, and folders such as Windows, Program Files, AppData and `node_modules`, are skipped unless you turn them on.
- Save named scopes, such as "Projects" for `D:\projects` and `E:\work`.

### Everyday use
- Global hotkey to open the search window, and a tray icon.
- Start with Windows, running quietly in the tray.
- Keyboard-driven: `Ctrl+D` scope, `Ctrl+M` match mode, `Ctrl+E` extensions, `Ctrl+P` folders, `Ctrl+I` match case, `Ctrl+1` to `Ctrl+9` toggle type chips.
- Light, dark or follow-Windows theme.
- Option to forget which files you have opened.

## Requirements

- Windows 10 (build 17763) or later, 64-bit.
- To build: the .NET 8 SDK and, for the installer, [Inno Setup](https://jrsoftware.org/isinfo.php).

## Build and run

```powershell
dotnet build EverySearch.csproj -c Release
dotnet run --project EverySearch.csproj
```

## Tests

```powershell
dotnet test tests\EverySearch.Tests\EverySearch.Tests.csproj
```

## Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

This publishes a self-contained build to `publish\` and compiles `installer\EverySearch.iss` with Inno Setup. The installer is written to `installer\Output\`.

## Settings

Settings are stored as JSON in `%AppData%\EverySearch\settings.json`. Open them from the palette with `>settings`.

## Project layout

| Path | What it holds |
|------|---------------|
| `MainWindow.xaml*` | The search window |
| `Services/FileIndex.cs` | In-memory index, crawl, snapshot and watchers |
| `Services/Query.cs` | Query parsing and matching |
| `Services/ContentSearch.cs` | Search inside files |
| `Services/FileTypes.cs` | Type groups, chips and ranking tiers |
| `Services/NativeShell.cs` | Global hotkey and tray icon |
| `Services/UsageStore.cs`, `Services/AppSettings.cs` | Opened-file history and settings |
| `Assets/Types/` | File-type icons |
| `tests/EverySearch.Tests` | Engine tests |

## Author

Arman Soltani
