namespace EverySearch;

[Flags]
public enum TypeKind
{
    None = 0,
    Folders = 1,
    Documents = 2,
    Images = 4,
    Video = 8,
    Audio = 16,
    Archives = 32,
    Code = 64,
    Executables = 128,
    Other = 256,
    Cad = 512,
    /// <summary>Not a chip: extensions that belong to the operating system or build output (dll, sys, mui...).</summary>
    System = 1024,
}

/// <summary>Maps file extensions to the type groups offered as filter chips, and to a ranking tier.</summary>
public static class FileTypes
{
    /// <summary>Chips in display order; Ctrl+1..9 toggle the first nine.</summary>
    public static readonly (TypeKind Kind, string Label, string Glyph)[] All =
    {
        (TypeKind.Folders, "Folders", ""),
        (TypeKind.Documents, "Documents", ""),
        (TypeKind.Images, "Images", ""),
        (TypeKind.Video, "Video", ""),
        (TypeKind.Audio, "Audio", ""),
        (TypeKind.Cad, "CAD", ""),
        (TypeKind.Archives, "Archives", ""),
        (TypeKind.Code, "Code", ""),
        (TypeKind.Executables, "Apps", ""),
        (TypeKind.Other, "Other", ""),
    };

    // Keyed by a hash of the lower-cased extension so lookups need no string allocation on the hot path.
    private static readonly Dictionary<int, (string Ext, TypeKind Kind)> ByHash = Build();
    private const int MaxExtLength = 12;

    private static Dictionary<int, (string, TypeKind)> Build()
    {
        var map = new Dictionary<int, (string, TypeKind)>();
        void Add(TypeKind kind, string list)
        {
            foreach (var ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[Hash(ext)] = (ext, kind);
        }
        Add(TypeKind.Documents, "pdf doc docx docm dot dotx xls xlsx xlsm ppt pptx pps ppsx txt rtf odt ods odp csv md epub mobi xps tex log pages numbers key vsdx");
        Add(TypeKind.Images, "jpg jpeg jfif png gif bmp tif tiff webp avif svg ico heic heif raw cr2 cr3 nef arw dng psd ai xcf eps");
        Add(TypeKind.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp mts m2ts vob ogv");
        Add(TypeKind.Audio, "mp3 wav flac aac ogg m4a m4b wma opus aiff mid midi wv amr");
        Add(TypeKind.Cad, "dwg dxf dwf dwt rvt rfa ifc skp stl step stp iges igs 3ds fbx blend max dgn sldprt sldasm slddrw ipt iam f3d 3dm obj");
        Add(TypeKind.Archives, "zip rar 7z tar gz bz2 xz iso cab tgz zst img");
        Add(TypeKind.Code, "cs js jsx ts tsx py java c cpp h hpp rs go rb php html htm css scss json xml yml yaml toml sh ps1 sql kt swift vue xaml sln csproj lua dart");
        Add(TypeKind.Executables, "exe msi com scr lnk bat cmd appx msix apk");
        Add(TypeKind.System, "dll sys mui manifest cat mum etl pf tmp drv ocx cpl efi nls mof inf ini dat idx pdb lib exp res cache lock bin dmp");
        return map;
    }

    private static int Hash(ReadOnlySpan<char> ext)
    {
        uint h = 2166136261;
        foreach (char c in ext) h = (h ^ char.ToLowerInvariant(c)) * 16777619;
        return (int)h;
    }

    /// <summary>The extension (no dot) as a slice of <paramref name="name"/>, or empty when there is none.</summary>
    public static ReadOnlySpan<char> ExtensionSpan(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name.AsSpan(dot + 1) : default;
    }

    /// <summary>Lower-case extension without the dot, or "" when there is none.</summary>
    public static string ExtensionOf(string name) => ExtensionSpan(name).ToString().ToLowerInvariant();

    public static TypeKind KindOf(ReadOnlySpan<char> ext)
    {
        if (ext.Length == 0 || ext.Length > MaxExtLength) return TypeKind.Other;
        if (!ByHash.TryGetValue(Hash(ext), out var hit)) return TypeKind.Other;
        return ext.Equals(hit.Ext, StringComparison.OrdinalIgnoreCase) ? hit.Kind : TypeKind.Other;
    }

    public static TypeKind KindOf(string extension) => KindOf(extension.AsSpan());

    /// <summary>Kind of an entry. Folders are Folders; unknown extensions are Other.</summary>
    public static TypeKind KindOfEntry(string name, bool isDir) => isDir ? TypeKind.Folders : KindOf(ExtensionSpan(name));

    /// <summary>0 = what people work with (documents, media, CAD, folders) ... 4 = system/build noise. Lower ranks higher.</summary>
    public static int TierOf(TypeKind kind) => kind switch
    {
        TypeKind.Folders or TypeKind.Documents or TypeKind.Images or TypeKind.Video or TypeKind.Audio or TypeKind.Cad => 0,
        TypeKind.Archives => 1,
        TypeKind.Code or TypeKind.Executables => 2,
        TypeKind.System => 4,
        _ => 3,
    };

    /// <summary>File name (in Assets\Types) of the icon drawn for this kind.</summary>
    public static string IconFile(TypeKind kind) => kind switch
    {
        TypeKind.Folders => "folder.png",
        TypeKind.Documents => "document.png",
        TypeKind.Images => "image.png",
        TypeKind.Video => "video.png",
        TypeKind.Audio => "audio.png",
        TypeKind.Cad => "cad.png",
        TypeKind.Archives => "archive.png",
        TypeKind.Code => "code.png",
        TypeKind.Executables => "app.png",
        _ => "other.png",
    };

    public static string Label(TypeKind kind) => kind switch
    {
        TypeKind.Folders => "Folder",
        TypeKind.Documents => "Document",
        TypeKind.Images => "Image",
        TypeKind.Video => "Video",
        TypeKind.Audio => "Audio",
        TypeKind.Cad => "CAD / 3D",
        TypeKind.Archives => "Archive",
        TypeKind.Code => "Code",
        TypeKind.Executables => "App",
        TypeKind.System => "System file",
        _ => "File",
    };

    public static (TypeKind Kind, string Glyph) Describe(string name, bool isDir)
    {
        var kind = KindOfEntry(name, isDir);
        foreach (var (k, _, glyph) in All)
            if (k == kind) return (kind, glyph);
        return (kind, "");
    }

    /// <summary>Accepts friendly names for the "type:" operator ("image", "pics", "exe", ...).</summary>
    public static bool TryParseKind(string word, out TypeKind kind)
    {
        kind = word.Trim().ToLowerInvariant() switch
        {
            "folder" or "folders" or "dir" or "dirs" => TypeKind.Folders,
            "doc" or "docs" or "document" or "documents" => TypeKind.Documents,
            "image" or "images" or "picture" or "pictures" or "pic" or "pics" => TypeKind.Images,
            "video" or "videos" or "movie" or "movies" => TypeKind.Video,
            "audio" or "music" or "sound" => TypeKind.Audio,
            "cad" or "3d" or "drawing" or "drawings" => TypeKind.Cad,
            "archive" or "archives" or "zip" => TypeKind.Archives,
            "code" or "source" => TypeKind.Code,
            "exe" or "app" or "apps" or "executable" or "executables" => TypeKind.Executables,
            "other" => TypeKind.Other,
            _ => TypeKind.None,
        };
        return kind != TypeKind.None;
    }
}
