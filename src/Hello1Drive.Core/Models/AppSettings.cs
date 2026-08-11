namespace Hello1Drive.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public enum WindowBackgroundMode
{
    Default,
    Color,
    LocalImage,
    Url,
    LocalFolder,
    OneDriveFolder
}

public enum FileViewMode
{
    Details,
    LargeIcons,
    ExtraLargeIcons
}

public enum FileSortColumn
{
    None = 0,
    Name = 1,

    // Legacy value kept only so settings written by older builds (Type = 2)
    // can be recognized and discarded safely without shifting enum numbers.
    LegacyType = 2,

    Size = 3,
    Modified = 4
}

public enum SortCycleState
{
    Original,
    Ascending,
    Descending
}

public enum PreviewKind
{
    None,
    Text,
    Image,
    Media,
    Generic
}

public sealed class RememberedBreadcrumb
{
    public string Name { get; set; } = string.Empty;
    public string? ItemId { get; set; }
}

public sealed class RememberedFolderSortRule
{
    public string FolderKey { get; set; } = string.Empty;
    public FileSortColumn Column { get; set; } = FileSortColumn.None;
    public SortCycleState State { get; set; } = SortCycleState.Original;
}

public sealed class RememberedFolderViewMode
{
    public string FolderKey { get; set; } = string.Empty;
    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;
}

public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;
    public WindowBackgroundMode BackgroundMode { get; set; } = WindowBackgroundMode.Default;
    public string BackgroundColor { get; set; } = "#F7F7F8";
    public string BackgroundUrl { get; set; } = string.Empty;
    public string LocalImageBookmark { get; set; } = string.Empty;
    public string LocalImageDisplayName { get; set; } = string.Empty;
    public string LocalFolderBookmark { get; set; } = string.Empty;
    public string LocalFolderDisplayName { get; set; } = string.Empty;
    public string OneDriveBackgroundFolderId { get; set; } = string.Empty;
    public string OneDriveBackgroundFolderName { get; set; } = string.Empty;
    public double BackgroundIntervalMinutes { get; set; } = 5;
    public double AcrylicBlurPercent { get; set; } = 50;

    // Navigation / UI persistence
    public bool RememberLastFolder { get; set; } = true;
    public List<RememberedBreadcrumb> LastFolderBreadcrumbs { get; set; } = [];
    public List<RememberedFolderSortRule> FolderSortRules { get; set; } = [];
    public List<RememberedFolderViewMode> FolderViewModes { get; set; } = [];
    public bool ShowFloatingUploadButton { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;
    public bool TransparentFileItemBackground { get; set; }
    public bool ConfirmBeforeDelete { get; set; } = true;
    public bool UseBuiltInViewer { get; set; } = true;
    public bool StartWithWindows { get; set; }

    // Global default sort. Individual folders may override this through FolderSortRules.
    // Changing the global default clears all per-folder overrides.
    public FileSortColumn DefaultSortColumn { get; set; } = FileSortColumn.None;
    public SortCycleState DefaultSortState { get; set; } = SortCycleState.Original;

    public double SlideshowIntervalSeconds { get; set; } = 5;

    // Transfer throttling. Values are KB/s and only applied when the matching switch is enabled.
    public bool LimitDownloadSpeed { get; set; }
    public double DownloadSpeedLimitKBps { get; set; } = 1024;
    public bool LimitUploadSpeed { get; set; }
    public double UploadSpeedLimitKBps { get; set; } = 1024;

    // Floating upload button position, saved as 0..1 normalized coordinates so it
    // stays useful after the window or mobile orientation changes.
    public double FloatingUploadX { get; set; } = 0.94;
    public double FloatingUploadY { get; set; } = 0.90;
}
