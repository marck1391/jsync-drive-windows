using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using JsyncDrive.Engine;

namespace JsyncDrive.Ui;

public enum FileKind { Folder, Image, Video, Audio, Pdf, Document, Sheet, Slides, Archive, Code, Text, Other }

public static class FileKinds
{
    public static FileKind Of(string name, bool dir)
    {
        if (dir) return FileKind.Folder;
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif" or ".tif" or ".tiff" or ".svg" => FileKind.Image,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".m4v" => FileKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".opus" => FileKind.Audio,
            ".pdf" => FileKind.Pdf,
            ".doc" or ".docx" or ".odt" or ".rtf" => FileKind.Document,
            ".xls" or ".xlsx" or ".ods" or ".csv" => FileKind.Sheet,
            ".ppt" or ".pptx" or ".odp" => FileKind.Slides,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => FileKind.Archive,
            ".cs" or ".go" or ".js" or ".ts" or ".tsx" or ".py" or ".java" or ".kt" or ".rs" or ".c" or ".cpp" or ".h" or ".json" or ".xml" or ".html" or ".css" or ".sh" or ".ps1" or ".yml" or ".yaml" or ".sql" => FileKind.Code,
            ".txt" or ".md" or ".log" => FileKind.Text,
            _ => FileKind.Other,
        };
    }

    /// <summary>Images the Drive can show as a thumbnail (decoded by Windows).</summary>
    public static bool CanThumb(string name, long size) =>
        size is > 0 and <= 12_000_000 && Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff";

    public static string Icon(FileKind k) => k switch
    {
        FileKind.Folder => "IconFolder",
        FileKind.Image => "IconImage",
        _ => "IconFile",
    };

    public static Color Tint(FileKind k) => k switch
    {
        FileKind.Image => Color.FromRgb(0x1E, 0x8E, 0x8E),
        FileKind.Video => Color.FromRgb(0x8E, 0x4C, 0xC4),
        FileKind.Audio => Color.FromRgb(0xC4, 0x4C, 0x8E),
        FileKind.Pdf => Color.FromRgb(0xD0, 0x40, 0x3A),
        FileKind.Document => Color.FromRgb(0x2B, 0x5B, 0xC4),
        FileKind.Sheet => Color.FromRgb(0x1E, 0x8E, 0x3E),
        FileKind.Slides => Color.FromRgb(0xE0, 0x7A, 0x1A),
        FileKind.Archive => Color.FromRgb(0xB2, 0x8A, 0x00),
        FileKind.Code => Color.FromRgb(0x4C, 0x6A, 0x8E),
        _ => Color.FromRgb(0x7A, 0x86, 0x99),
    };

    public static Color DefaultFolder => Color.FromRgb(0x7B, 0x96, 0xC9);

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();

    public static SolidColorBrush BrushOf(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); Brushes[c] = b; }
        return b;
    }

    public static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return null; }
    }
}

/// <summary>A row of the Drive browser.</summary>
public sealed class EntryVm : INotifyPropertyChanged
{
    public Entry E { get; }
    public string FullPath { get; }
    public FileKind Kind { get; }

    public EntryVm(Entry e, string parent)
    {
        E = e;
        FullPath = e.Path ?? (parent.Length == 0 ? e.Name : parent + "/" + e.Name);
        Kind = FileKinds.Of(e.Name, e.Dir);
    }

    public string Name => E.Name;
    /// <summary>Folders and files are listed in their own groups.</summary>
    public string Section => E.Dir ? Loc.T("Folders") : Loc.T("Files");
    public bool IsDir => E.Dir;
    public bool IsLocked => E.Protected && E.Locked;
    public bool IsProtected => E.Protected;
    public string SizeText => E.Dir ? "" : Fmt.Size(E.Size);
    public string ModifiedText => E.Modified is { } m ? Fmt.Ago(m) : "";
    /// <summary>For a search result: the folder it is in.</summary>
    public string? Context { get; set; }
    public string Subtitle => Context != null ? Context : E.Dir ? ModifiedText : (SizeText + (ModifiedText.Length > 0 ? " · " + ModifiedText : ""));
    public Geometry IconGeometry => (Geometry)Application.Current.FindResource(IsLocked ? "IconLock" : FileKinds.Icon(Kind));
    public bool CanThumb => FileKinds.CanThumb(E.Name, E.Size);

    private string? _colorHex;
    public string? ColorHex { get => _colorHex; set { _colorHex = value; Raise(nameof(IconBrush)); } }

    private bool _pinned;
    public bool Pinned { get => _pinned; set { _pinned = value; Raise(nameof(Pinned)); } }

    public Brush IconBrush => Kind == FileKind.Folder
        ? FileKinds.BrushOf(FileKinds.ParseHex(_colorHex) ?? FileKinds.DefaultFolder)
        : FileKinds.BrushOf(FileKinds.Tint(Kind));

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); Raise(nameof(HasThumb)); Raise(nameof(NoThumb)); } }
    public bool HasThumb => _thumb != null;
    public bool NoThumb => _thumb == null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
