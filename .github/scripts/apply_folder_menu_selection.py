from pathlib import Path
import re


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# -----------------------------------------------------------------------------
# Defaults and JSON persistence model
# -----------------------------------------------------------------------------
path = "src/Hello1Drive.Core/Models/AppSettings.cs"
text = read(path)
text = replace_once(
    text,
    "    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;\n}",
    "    public FileViewMode ViewMode { get; set; } = FileViewMode.LargeIcons;\n}",
    "remembered folder view default",
)
text = replace_once(
    text,
    "    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;\n    public WindowBackgroundMode BackgroundMode",
    "    public FileViewMode ViewMode { get; set; } = FileViewMode.LargeIcons;\n    public WindowBackgroundMode BackgroundMode",
    "app view default",
)
write(path, text)

path = "src/Hello1Drive.Core/Services/AppSettingsService.cs"
text = read(path)
needle = """        Current.FolderViewModes = Current.FolderViewModes
            .Where(x => !string.IsNullOrWhiteSpace(x.FolderKey) &&
                        Enum.IsDefined(typeof(FileViewMode), x.ViewMode))
            .GroupBy(x => x.FolderKey, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
"""
replacement = needle + """
        // View mode is no longer a mutable global fallback. Every folder has its own
        // remembered choice; folders without a saved rule always start in Large Icons.
        // Normalize the legacy fallback as well so older settings.json files migrate cleanly.
        Current.ViewMode = FileViewMode.LargeIcons;
"""
text = replace_once(text, needle, replacement, "settings view-mode migration")
write(path, text)

# -----------------------------------------------------------------------------
# ViewModel: per-folder defaults, persistence, and selected-state bindings
# -----------------------------------------------------------------------------
path = "src/Hello1Drive.Core/ViewModels/MainViewModel.cs"
text = read(path)
text = replace_once(
    text,
    "    [ObservableProperty] private FileViewMode viewMode = FileViewMode.Details;",
    "    [ObservableProperty] private FileViewMode viewMode = FileViewMode.LargeIcons;",
    "view model view default",
)

view_props = """    public bool IsDetailsView => ViewMode == FileViewMode.Details;
    public bool IsLargeIconView => ViewMode == FileViewMode.LargeIcons;
    public bool IsExtraLargeIconView => ViewMode == FileViewMode.ExtraLargeIcons;
"""
view_props_new = view_props + """
    public bool IsSystemDefaultSort => SortState == SortCycleState.Original || SortColumn == FileSortColumn.None;
    public bool IsNameAscendingSort => SortColumn == FileSortColumn.Name && SortState == SortCycleState.Ascending;
    public bool IsNameDescendingSort => SortColumn == FileSortColumn.Name && SortState == SortCycleState.Descending;
    public bool IsModifiedAscendingSort => SortColumn == FileSortColumn.Modified && SortState == SortCycleState.Ascending;
    public bool IsModifiedDescendingSort => SortColumn == FileSortColumn.Modified && SortState == SortCycleState.Descending;
    public bool IsSizeAscendingSort => SortColumn == FileSortColumn.Size && SortState == SortCycleState.Ascending;
    public bool IsSizeDescendingSort => SortColumn == FileSortColumn.Size && SortState == SortCycleState.Descending;
"""
text = replace_once(text, view_props, view_props_new, "selection-state properties")

old_set_view = """    public async Task SetViewModeAsync(FileViewMode mode)
    {
        ViewMode = mode;

        // Keep the existing setting as the fallback for a folder that has never been visited,
        // and remember the explicit choice for this account + folder independently.
        Settings.ViewMode = mode;
        RememberCurrentFolderViewMode();
        await _settingsService.SaveAsync();
    }
"""
new_set_view = """    public async Task SetViewModeAsync(FileViewMode mode)
    {
        ViewMode = mode;

        // View mode is strictly per account + folder. Do not mutate a global fallback when
        // one folder changes; an unremembered folder always starts in Large Icons.
        RememberCurrentFolderViewMode();
        await _settingsService.SaveAsync();
    }
"""
text = replace_once(text, old_set_view, new_set_view, "set view mode semantics")

text = replace_once(
    text,
    "        ViewMode = remembered?.ViewMode ?? Settings.ViewMode;",
    "        ViewMode = remembered?.ViewMode ?? FileViewMode.LargeIcons;",
    "restore folder view fallback",
)

old_default_sort = """    public async Task UseDefaultSortForCurrentFolderAsync()
    {
        var key = CurrentFolderSortMemoryKey();
        Settings.FolderSortRules.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));
        ApplyGlobalDefaultSortToCurrentState();
        await _settingsService.SaveAsync();

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }
"""
new_default_sort = """    public async Task UseDefaultSortForCurrentFolderAsync()
    {
        // “系统默认” is an explicit folder choice: use the OneDrive/API original order and
        // persist it just like every other sort option. It must not silently inherit a global rule.
        SortColumn = FileSortColumn.None;
        SortState = SortCycleState.Original;
        await PersistCurrentFolderSortRuleAsync();

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }
"""
text = replace_once(text, old_default_sort, new_default_sort, "system-default folder sort")

old_persist_sort = """    private async Task PersistCurrentFolderSortRuleAsync()
    {
        var key = CurrentFolderSortMemoryKey();
        Settings.FolderSortRules.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));

        // Always persist the folder choice, including API-original order.
        // This is what lets one folder override a non-default global sort.
        Settings.FolderSortRules.Add(new RememberedFolderSortRule
        {
            FolderKey = key,
            Column = SortState == SortCycleState.Original ? FileSortColumn.None : SortColumn,
            State = SortState
        });

        await _settingsService.SaveAsync();
    }
"""
new_persist_sort = """    private void RememberCurrentFolderSortRule()
    {
        var key = CurrentFolderSortMemoryKey();
        Settings.FolderSortRules.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));

        Settings.FolderSortRules.Add(new RememberedFolderSortRule
        {
            FolderKey = key,
            Column = SortState == SortCycleState.Original ? FileSortColumn.None : SortColumn,
            State = SortState
        });
    }

    private async Task PersistCurrentFolderSortRuleAsync()
    {
        // Always persist the folder choice, including API-original order.
        RememberCurrentFolderSortRule();
        await _settingsService.SaveAsync();
    }
"""
text = replace_once(text, old_persist_sort, new_persist_sort, "folder sort persistence helper")

old_global_sort = """        // The user explicitly asked that changing the setting overwrite all
        // folder-specific rules. New per-folder overrides can be created afterwards.
        Settings.FolderSortRules.Clear();
        ApplyGlobalDefaultSortToCurrentState();
        await _settingsService.SaveAsync();

        if (!IsAuthenticated)
            return;

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
"""
new_global_sort = """        // A default only applies to folders that have never recorded their own choice.
        // Existing per-folder rules remain independent and must never be erased here.
        var currentKey = CurrentFolderSortMemoryKey();
        var currentHasOwnRule = Settings.FolderSortRules.Any(
            x => string.Equals(x.FolderKey, currentKey, StringComparison.Ordinal));
        if (!currentHasOwnRule)
            ApplyGlobalDefaultSortToCurrentState();
        await _settingsService.SaveAsync();

        if (!IsAuthenticated || currentHasOwnRule)
            return;

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
"""
text = replace_once(text, old_global_sort, new_global_sort, "global sort preserves folder rules")

old_raise = """    private void RaiseSortIndicators()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ModifiedSortIndicator));
    }
"""
new_raise = """    private void RaiseSortIndicators()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ModifiedSortIndicator));
        OnPropertyChanged(nameof(IsSystemDefaultSort));
        OnPropertyChanged(nameof(IsNameAscendingSort));
        OnPropertyChanged(nameof(IsNameDescendingSort));
        OnPropertyChanged(nameof(IsModifiedAscendingSort));
        OnPropertyChanged(nameof(IsModifiedDescendingSort));
        OnPropertyChanged(nameof(IsSizeAscendingSort));
        OnPropertyChanged(nameof(IsSizeDescendingSort));
    }
"""
text = replace_once(text, old_raise, new_raise, "sort selected-state notifications")

old_begin_nav = """        // Capture the view of the folder we are leaving before Breadcrumbs changes. This makes
        // even inherited/default views become an explicit per-folder memory after the first visit.
        RememberCurrentFolderViewMode();
        _ = _settingsService.SaveAsync();
"""
new_begin_nav = """        // Capture the view and sort of the folder we are leaving before Breadcrumbs changes.
        // Even untouched defaults become explicit per-folder JSON after the first visit.
        RememberCurrentFolderViewMode();
        RememberCurrentFolderSortRule();
        _ = _settingsService.SaveAsync();
"""
text = replace_once(text, old_begin_nav, new_begin_nav, "remember folder state on navigation")

text = replace_once(
    text,
    "        ViewMode = s.ViewMode;",
    "        ViewMode = FileViewMode.LargeIcons;",
    "load settings initial view",
)

old_apply_items = """        SetSelectedItems([]);
        if (RememberLastFolder)
        {
            CaptureCurrentFolderMemory();
            _ = _settingsService.SaveAsync();
        }

        if (IsAuthenticated && !string.IsNullOrWhiteSpace(CurrentAccountId))
"""
new_apply_items = """        SetSelectedItems([]);

        // Persist the effective state for every folder that is actually presented, including the
        // Large Icons / System Default first-visit defaults. This keeps restart behavior deterministic.
        RememberCurrentFolderViewMode();
        RememberCurrentFolderSortRule();
        if (RememberLastFolder)
            CaptureCurrentFolderMemory();
        _ = _settingsService.SaveAsync();

        if (IsAuthenticated && !string.IsNullOrWhiteSpace(CurrentAccountId))
"""
text = replace_once(text, old_apply_items, new_apply_items, "persist effective folder state")
write(path, text)

# -----------------------------------------------------------------------------
# Desktop + mobile menu visuals: fixed leading slot with an accent dot.
# -----------------------------------------------------------------------------
path = "src/Hello1Drive.Core/Views/MainView.axaml"
text = read(path)


def header_markup(label: str, binding: str, indent: str = "") -> str:
    return (
        f'{indent}<MenuItem.Header>\n'
        f'{indent}  <Grid ColumnDefinitions="10,*" ColumnSpacing="6">\n'
        f'{indent}    <Ellipse Width="6" Height="6" Fill="{{DynamicResource HelloAccentBrush}}" '
        f'HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="{{Binding {binding}}}" />\n'
        f'{indent}    <TextBlock Grid.Column="1" Text="{label}" VerticalAlignment="Center" />\n'
        f'{indent}  </Grid>\n'
        f'{indent}</MenuItem.Header>'
    )


def decorate_menu_items(source: str, label: str, tag: str, click: str, binding: str) -> str:
    open_token = f'<MenuItem Header="{label}" Tag="{tag}" Click="{click}">'
    self_token = f'<MenuItem Header="{label}" Tag="{tag}" Click="{click}" />'
    total = source.count(open_token) + source.count(self_token)
    if total == 0:
        raise RuntimeError(f"desktop menu item not found: {label}/{tag}/{click}")

    # Preserve the existing item icon/body when the item already has children.
    source = source.replace(
        open_token,
        f'<MenuItem Tag="{tag}" Click="{click}">\n                  ' + header_markup(label, binding, "")
    )

    # Self-closing sort items become normal MenuItems containing only the custom Header.
    source = source.replace(
        self_token,
        f'<MenuItem Tag="{tag}" Click="{click}">\n                  '
        + header_markup(label, binding, "")
        + '\n                </MenuItem>'
    )
    return source


for label, tag, binding in [
    ("详细信息", "Details", "IsDetailsView"),
    ("大图标", "LargeIcons", "IsLargeIconView"),
    ("超大图标", "ExtraLargeIcons", "IsExtraLargeIconView"),
]:
    text = decorate_menu_items(text, label, tag, "ViewContextMenu_Click", binding)

for label, tag, binding in [
    ("系统默认", "Inherit:Default", "IsSystemDefaultSort"),
    ("名称 · 升序", "Name:Ascending", "IsNameAscendingSort"),
    ("名称 · 降序", "Name:Descending", "IsNameDescendingSort"),
    ("日期 · 升序", "Modified:Ascending", "IsModifiedAscendingSort"),
    ("日期 · 降序", "Modified:Descending", "IsModifiedDescendingSort"),
    ("大小 · 升序", "Size:Ascending", "IsSizeAscendingSort"),
    ("大小 · 降序", "Size:Descending", "IsSizeDescendingSort"),
]:
    text = decorate_menu_items(text, label, tag, "SortMenu_Click", binding)


def decorate_mobile_button(source: str, label: str, tag: str, click: str, binding: str) -> str:
    pattern = re.compile(
        rf'<Button\s+Content="{re.escape(label)}"\s+Tag="{re.escape(tag)}"(?P<attrs>.*?)Click="{re.escape(click)}"\s*/>',
        re.S,
    )
    matches = list(pattern.finditer(source))
    if len(matches) != 1:
        raise RuntimeError(f"mobile action item {label}: expected 1 match, found {len(matches)}")

    def repl(match: re.Match) -> str:
        attrs = match.group("attrs")
        return (
            f'<Button Tag="{tag}"{attrs}Click="{click}">\n'
            '            <Button.Content>\n'
            '              <Grid ColumnDefinitions="12,*" ColumnSpacing="8">\n'
            f'                <Ellipse Width="7" Height="7" Fill="{{DynamicResource HelloAccentBrush}}" '
            f'HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="{{Binding {binding}}}" />\n'
            f'                <TextBlock Grid.Column="1" Text="{label}" Foreground="White" VerticalAlignment="Center" />\n'
            '              </Grid>\n'
            '            </Button.Content>\n'
            '          </Button>'
        )

    return pattern.sub(repl, source, count=1)


for label, tag, binding in [
    ("详细信息", "Details", "IsDetailsView"),
    ("大图标", "LargeIcons", "IsLargeIconView"),
    ("超大图标", "ExtraLargeIcons", "IsExtraLargeIconView"),
]:
    text = decorate_mobile_button(text, label, tag, "MobileViewModeAction_Click", binding)

for label, tag, binding in [
    ("系统默认", "Inherit:Default", "IsSystemDefaultSort"),
    ("名称 · 升序", "Name:Ascending", "IsNameAscendingSort"),
    ("名称 · 降序", "Name:Descending", "IsNameDescendingSort"),
    ("日期 · 升序", "Modified:Ascending", "IsModifiedAscendingSort"),
    ("日期 · 降序", "Modified:Descending", "IsModifiedDescendingSort"),
    ("大小 · 升序", "Size:Ascending", "IsSizeAscendingSort"),
    ("大小 · 降序", "Size:Descending", "IsSizeDescendingSort"),
]:
    text = decorate_mobile_button(text, label, tag, "MobileSortAction_Click", binding)

write(path, text)
print("Folder-specific menu selection patch applied successfully.")
