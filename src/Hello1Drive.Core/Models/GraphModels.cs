using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hello1Drive.Models;

public sealed class GraphCollectionResponse<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}


public sealed class DriveItemPage
{
    public IReadOnlyList<DriveItemModel> Items { get; init; } = Array.Empty<DriveItemModel>();
    public string? NextLink { get; init; }
    public bool HasMore => !string.IsNullOrWhiteSpace(NextLink);
}

public sealed class GraphUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("mail")]
    public string? Mail { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }

    public string DisplayEmail => Mail ?? UserPrincipalName ?? string.Empty;
}

public sealed class DriveInfoModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("driveType")]
    public string? DriveType { get; set; }

    [JsonPropertyName("quota")]
    public DriveQuota? Quota { get; set; }
}

public sealed class DriveQuota
{
    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("used")]
    public long? Used { get; set; }

    [JsonPropertyName("remaining")]
    public long? Remaining { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

public sealed class DriveItemModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".csv", ".log", ".ini", ".yaml", ".yml",
        ".cs", ".fs", ".vb", ".xaml", ".axaml", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm",
        ".css", ".scss", ".less", ".py", ".java", ".kt", ".kts", ".cpp", ".c", ".h", ".hpp",
        ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd", ".sql", ".toml", ".properties", ".config"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".flv", ".3gp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma"
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz"
    };

    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".rtf"
    };

    private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsx", ".xlsm", ".xlsb"
    };

    private static readonly HashSet<string> PowerPointExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ppt", ".pptx", ".pptm"
    };

    private static readonly HashSet<string> UrlShortcutExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".url", ".webloc", ".website"
    };

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("webUrl")]
    public string? WebUrl { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTimeOffset? CreatedDateTime { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    [JsonPropertyName("eTag")]
    public string? ETag { get; set; }

    [JsonPropertyName("cTag")]
    public string? CTag { get; set; }

    [JsonPropertyName("folder")]
    public FolderFacet? Folder { get; set; }

    [JsonPropertyName("file")]
    public FileFacet? File { get; set; }

    [JsonPropertyName("remoteItem")]
    public RemoteItemFacet? RemoteItem { get; set; }

    [JsonPropertyName("thumbnails")]
    public List<ThumbnailSetModel> Thumbnails { get; set; } = [];

    private Bitmap? _thumbnailImage;

    [JsonIgnore]
    public Bitmap? ThumbnailImage
    {
        get => _thumbnailImage;
        set
        {
            if (!SetProperty(ref _thumbnailImage, value))
                return;
            OnPropertyChanged(nameof(HasThumbnailImage));
            OnPropertyChanged(nameof(HasNoThumbnailImage));
            OnPropertyChanged(nameof(ShowVideoThumbnailBadge));
        }
    }

    public bool IsFolder => Folder is not null || RemoteItem?.Folder is not null;
    public bool IsFile => !IsFolder;
    public int ChildCount => Folder?.ChildCount ?? RemoteItem?.Folder?.ChildCount ?? 0;
    public string MimeType => File?.MimeType ?? RemoteItem?.File?.MimeType ?? string.Empty;
    public string Extension => Path.GetExtension(Name);

    public bool IsImage => IsFile && (MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(Extension));
    public bool IsVideo => IsFile && (MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(Extension));
    public bool IsAudio => IsFile && (MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(Extension));
    public bool IsPdf => IsFile && (MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) || PdfExtensions.Contains(Extension));
    public bool IsArchive => IsFile && ArchiveExtensions.Contains(Extension);
    public bool IsWord => IsFile && (MimeType.Contains("word", StringComparison.OrdinalIgnoreCase) || WordExtensions.Contains(Extension));
    public bool IsExcel => IsFile && (MimeType.Contains("excel", StringComparison.OrdinalIgnoreCase) || MimeType.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase) || ExcelExtensions.Contains(Extension));
    public bool IsPowerPoint => IsFile && (MimeType.Contains("powerpoint", StringComparison.OrdinalIgnoreCase) || MimeType.Contains("presentation", StringComparison.OrdinalIgnoreCase) || PowerPointExtensions.Contains(Extension));
    public bool IsUrlShortcut => IsFile && (MimeType.Equals("text/uri-list", StringComparison.OrdinalIgnoreCase) || UrlShortcutExtensions.Contains(Extension));
    public bool IsMedia => IsVideo || IsAudio;
    public bool IsText => IsFile && (MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || TextExtensions.Contains(Extension));
    public bool SupportsThumbnail => IsFile && (!string.IsNullOrWhiteSpace(ThumbnailUrl) || IsImage || IsVideo || IsPdf || IsWord || IsExcel || IsPowerPoint || IsText);
    public bool HasThumbnailImage => ThumbnailImage is not null;
    public bool HasNoThumbnailImage => ThumbnailImage is null;
    public bool ShowVideoThumbnailBadge => IsVideo && HasThumbnailImage;
    public bool HasWebUrl => !string.IsNullOrWhiteSpace(WebUrl);

    [JsonIgnore]
    public string VersionToken => !string.IsNullOrWhiteSpace(ETag)
        ? ETag!
        : !string.IsNullOrWhiteSpace(CTag)
            ? CTag!
            : $"{Size}|{LastModifiedDateTime?.UtcDateTime.Ticks ?? 0}|{Name}";

    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get
        {
            var set = Thumbnails.FirstOrDefault();
            return set?.Medium?.Url ?? set?.Small?.Url ?? set?.Large?.Url;
        }
    }

    public string TypeDisplay
    {
        get
        {
            if (IsFolder)
                return "文件夹";
            if (IsImage)
                return "图片";
            if (IsVideo)
                return "视频";
            if (IsAudio)
                return "音频";
            if (IsPdf)
                return "PDF";
            if (IsWord)
                return "Word";
            if (IsExcel)
                return "Excel";
            if (IsPowerPoint)
                return "PowerPoint";
            if (IsArchive)
                return "压缩包";
            if (IsUrlShortcut)
                return "快捷方式";
            if (IsText)
                return "文本";
            return string.IsNullOrWhiteSpace(Extension)
                ? (string.IsNullOrWhiteSpace(MimeType) ? "文件" : MimeType)
                : Extension.TrimStart('.').ToUpperInvariant();
        }
    }

    public string SizeDisplay => IsFolder ? $"{ChildCount} 项" : FormatBytes(Size);
    public string ModifiedDisplay => LastModifiedDateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public string IconText => IsFolder ? "📁" : IsImage ? "🖼" : IsVideo ? "▶" : IsAudio ? "♫" : FileBadgeText;

    public string FileBadgeText
    {
        get
        {
            if (IsFolder)
                return "DIR";
            if (IsPdf)
                return "PDF";
            if (IsWord)
                return "DOC";
            if (IsExcel)
                return "XLS";
            if (IsPowerPoint)
                return "PPT";
            if (IsArchive)
                return "ZIP";
            if (IsUrlShortcut)
                return "URL";
            if (IsText)
                return "TXT";
            if (IsAudio)
                return "MUS";
            var ext = Extension.TrimStart('.').ToUpperInvariant();
            return string.IsNullOrWhiteSpace(ext)
                ? "FILE"
                : ext.Length <= 4 ? ext : ext[..4];
        }
    }

    public bool IsGenericFile => IsFile && !IsImage && !IsVideo && !IsAudio && !IsPdf && !IsWord && !IsExcel && !IsPowerPoint && !IsArchive && !IsUrlShortcut && !IsText;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.##} {units[index]}";
    }

    public void Dispose()
    {
        ThumbnailImage?.Dispose();
        ThumbnailImage = null;
    }
}

public sealed class ThumbnailSetModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("small")]
    public ThumbnailModel? Small { get; set; }

    [JsonPropertyName("medium")]
    public ThumbnailModel? Medium { get; set; }

    [JsonPropertyName("large")]
    public ThumbnailModel? Large { get; set; }
}

public sealed class ThumbnailModel
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class FolderFacet
{
    [JsonPropertyName("childCount")]
    public int ChildCount { get; set; }
}

public sealed class FileFacet
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }
}

public sealed class RemoteItemFacet
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("folder")]
    public FolderFacet? Folder { get; set; }

    [JsonPropertyName("file")]
    public FileFacet? File { get; set; }
}

public sealed class UploadSessionResponse
{
    [JsonPropertyName("uploadUrl")]
    public string UploadUrl { get; set; } = string.Empty;

    [JsonPropertyName("expirationDateTime")]
    public DateTimeOffset? ExpirationDateTime { get; set; }
}

public sealed class DownloadUrlResponse
{
    [JsonPropertyName("@microsoft.graph.downloadUrl")]
    public string? DownloadUrl { get; set; }
}
