from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(relative_path: str, old: str, new: str) -> None:
    path = ROOT / relative_path
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected exactly one match in {relative_path}, got {count}: {old[:80]!r}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


android = "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"

replace_once(
    android,
    '''        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText))
        {
            UpdateTheme();
        }''',
    '''        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText) or
            nameof(MainViewModel.TransparentFileItemBackground))
        {
            UpdateTheme();
        }''')

replace_once(
    android,
    '''    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var background = dark ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
        _refresh.SetBackgroundColor(background);
        _recycler.SetBackgroundColor(background);
        _adapter.SetDarkTheme(dark);
    }''',
    '''    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var transparent = _viewModel?.TransparentFileItemBackground == true;
        var background = transparent
            ? Color.Transparent
            : dark ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
        _refresh.SetBackgroundColor(background);
        _recycler.SetBackgroundColor(background);
        _adapter.SetPresentation(dark, transparent);
    }''')

replace_once(
    android,
    '''    private bool _scrolling;
    private bool _darkTheme;
    private bool _selectionMode;''',
    '''    private bool _scrolling;
    private bool _darkTheme;
    private bool _transparentBackground;
    private bool _selectionMode;''')

replace_once(
    android,
    '''    public void SetDarkTheme(bool dark)
    {
        if (_darkTheme == dark)
            return;
        _darkTheme = dark;
        RefreshVisible();
    }''',
    '''    public void SetPresentation(bool dark, bool transparentBackground)
    {
        if (_darkTheme == dark && _transparentBackground == transparentBackground)
            return;
        _darkTheme = dark;
        _transparentBackground = transparentBackground;
        RefreshVisible();
    }''')

replace_once(
    android,
    '''        fileHolder.Bind(slot, Mode, _darkTheme, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cachedBitmap);''',
    '''        fileHolder.Bind(slot, Mode, _darkTheme, _transparentBackground, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cachedBitmap);''')

replace_once(
    android,
    '''        FileViewMode mode,
        bool darkTheme,
        bool selectionMode,
        bool selected,
        Bitmap? bitmap)''',
    '''        FileViewMode mode,
        bool darkTheme,
        bool transparentBackground,
        bool selectionMode,
        bool selected,
        Bitmap? bitmap)''')

replace_once(
    android,
    '''        _thumbnailRequestItemId = null;
        _view.Bind(slot.Item, mode, darkTheme, selectionMode, selected, bitmap);''',
    '''        _thumbnailRequestItemId = null;
        _view.Bind(slot.Item, mode, darkTheme, transparentBackground, selectionMode, selected, bitmap);''')

replace_once(
    android,
    '''        _thumbnailRequestItemId = null;
        _view.Bind(null, _view.Mode, _view.DarkTheme, false, false, null);''',
    '''        _thumbnailRequestItemId = null;
        _view.Bind(null, _view.Mode, _view.DarkTheme, _view.TransparentBackground, false, false, null);''')

replace_once(
    android,
    '''        Focusable = true;
        SetWillNotDraw(false);
        SetPadding(0, 0, 0, 0);''',
    '''        Focusable = true;
        SetBackgroundColor(Color.Transparent);
        SetWillNotDraw(false);
        SetPadding(0, 0, 0, 0);''')

replace_once(
    android,
    '''    public FileViewMode Mode { get; private set; } = FileViewMode.Details;
    public bool DarkTheme { get; private set; }

    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool selectionMode, bool selected, Bitmap? thumbnail)
    {
        var modeChanged = Mode != mode;
        _item = item;
        Mode = mode;
        DarkTheme = darkTheme;
        _selectionMode = selectionMode;''',
    '''    public FileViewMode Mode { get; private set; } = FileViewMode.Details;
    public bool DarkTheme { get; private set; }
    public bool TransparentBackground { get; private set; }

    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool transparentBackground, bool selectionMode, bool selected, Bitmap? thumbnail)
    {
        var modeChanged = Mode != mode;
        _item = item;
        Mode = mode;
        DarkTheme = darkTheme;
        TransparentBackground = transparentBackground;
        _selectionMode = selectionMode;''')

replace_once(
    android,
    '''        var bg = DarkTheme ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
        canvas.DrawColor(bg);

        if (_selected)''',
    '''        if (TransparentBackground)
        {
            // Clear the recycled native cell buffer so the Avalonia custom background below the
            // NativeControlHost remains visible instead of retaining a previous opaque frame.
            canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear);
        }
        else
        {
            var bg = DarkTheme ? Color.Rgb(18, 18, 18) : Color.Rgb(250, 250, 250);
            canvas.DrawColor(bg);
        }

        if (_selected)''')


ios = "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"

replace_once(
    ios,
    '''            AllowsSelection = true,
            AllowsMultipleSelection = false,
            BackgroundColor = UIColor.SystemBackground,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight''',
    '''            AllowsSelection = true,
            AllowsMultipleSelection = false,
            BackgroundColor = UIColor.Clear,
            Opaque = false,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight''')

replace_once(
    ios,
    '''        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText))
        {
            UpdateTheme();
        }''',
    '''        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.BackgroundColorText) or
            nameof(MainViewModel.SelectedBackgroundModeText) or
            nameof(MainViewModel.TransparentFileItemBackground))
        {
            UpdateTheme();
        }''')

replace_once(
    ios,
    '''    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var background = dark ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250);
        _collection.BackgroundColor = background;
        _refresh.TintColor = dark ? UIColor.White : UIColor.DarkGray;
        _source.SetDarkTheme(dark);
    }''',
    '''    private void UpdateTheme()
    {
        var dark = IsDarkTheme();
        var transparent = _viewModel?.TransparentFileItemBackground == true;
        var background = dark ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250);
        _collection.BackgroundColor = transparent ? UIColor.Clear : background;
        _collection.Opaque = !transparent;
        _refresh.TintColor = dark ? UIColor.White : UIColor.DarkGray;
        _source.SetPresentation(dark, transparent);
    }''')

replace_once(
    ios,
    '''    private bool _scrolling;
    private bool _darkTheme;
    private bool _selectionMode;''',
    '''    private bool _scrolling;
    private bool _darkTheme;
    private bool _transparentBackground;
    private bool _selectionMode;''')

replace_once(
    ios,
    '''    public void SetDarkTheme(bool dark)
    {
        if (_darkTheme == dark)
            return;
        _darkTheme = dark;
        RebindVisible();
    }''',
    '''    public void SetPresentation(bool dark, bool transparentBackground)
    {
        if (_darkTheme == dark && _transparentBackground == transparentBackground)
            return;
        _darkTheme = dark;
        _transparentBackground = transparentBackground;
        RebindVisible();
    }''')

replace_once(
    ios,
    '''        presenter.Bind(position, slot, _mode, _darkTheme, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cached);''',
    '''        presenter.Bind(position, slot, _mode, _darkTheme, _transparentBackground, _selectionMode,
            item is not null && _selectedIds.Contains(item.Id), cached);''')

replace_once(
    ios,
    '''        FileViewMode mode,
        bool darkTheme,
        bool selectionMode,
        bool selected,
        UIImage? image)''',
    '''        FileViewMode mode,
        bool darkTheme,
        bool transparentBackground,
        bool selectionMode,
        bool selected,
        UIImage? image)''')

replace_once(
    ios,
    '''        _thumbnailRequestItemId = null;
        _content.Bind(slot.Item, mode, darkTheme, selectionMode, selected, image);''',
    '''        _thumbnailRequestItemId = null;
        _content.Bind(slot.Item, mode, darkTheme, transparentBackground, selectionMode, selected, image);''')

replace_once(
    ios,
    '''    public IosNativeFileCellContentView(CGRect frame) : base(frame)
    {
        ClipsToBounds = true;
        Layer.CornerRadius = 8;''',
    '''    public IosNativeFileCellContentView(CGRect frame) : base(frame)
    {
        ClipsToBounds = true;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        Layer.CornerRadius = 8;''')

replace_once(
    ios,
    '''    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool selectionMode, bool selected, UIImage? image)
    {''',
    '''    public void Bind(DriveItemModel? item, FileViewMode mode, bool darkTheme, bool transparentBackground, bool selectionMode, bool selected, UIImage? image)
    {''')

replace_once(
    ios,
    '''        BackgroundColor = selected
            ? (darkTheme ? UIColor.FromRGBA(47, 128, 237, 77) : UIColor.FromRGBA(47, 128, 237, 36))
            : (darkTheme ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250));''',
    '''        BackgroundColor = selected
            ? (darkTheme ? UIColor.FromRGBA(47, 128, 237, 77) : UIColor.FromRGBA(47, 128, 237, 36))
            : transparentBackground
                ? UIColor.Clear
                : (darkTheme ? UIColor.FromRGB(18, 18, 18) : UIColor.FromRGB(250, 250, 250));''')

print("Native mobile list transparency patch applied.")
