from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
XAML = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml"
CS = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


xaml = XAML.read_text(encoding="utf-8")

# Desktop: replace the three independent view-mode buttons with one flyout button.
desktop_old = '''            <Button Classes="viewMode" ToolTip.Tip="详细信息" Tag="Details" Click="ViewModeButton_Click">
              <Path Classes="toolbarIcon iconView opticalUp" Data="M1.5,3 H4 M5.5,3 H12 M1.5,7 H4 M5.5,7 H12 M1.5,11 H4 M5.5,11 H12" />
            </Button>
            <Button Classes="viewMode" ToolTip.Tip="大图标" Tag="LargeIcons" Click="ViewModeButton_Click">
              <Path Classes="toolbarIcon iconView" Data="M1.5,1.5 H6 V6 H1.5 Z M8,1.5 H12.5 V6 H8 Z M1.5,8 H6 V12.5 H1.5 Z M8,8 H12.5 V12.5 H8 Z" />
            </Button>
            <Button Classes="viewMode" ToolTip.Tip="超大图标" Tag="ExtraLargeIcons" Click="ViewModeButton_Click">
              <Path Classes="toolbarIcon iconView" Data="M1.5,1.5 H7 V7 H1.5 Z M8.5,1.5 H12.5 V7 H8.5 Z M1.5,8.5 H7 V12.5 H1.5 Z M8.5,8.5 H12.5 V12.5 H8.5 Z" />
            </Button>'''

desktop_new = '''            <Button Classes="viewMode" ToolTip.Tip="查看方式">
              <Path Classes="toolbarIcon iconView opticalUp" Data="M1.5,3 H4 M5.5,3 H12 M1.5,7 H4 M5.5,7 H12 M1.5,11 H4 M5.5,11 H12" />
              <Button.Flyout>
                <MenuFlyout>
                  <MenuItem Header="详细信息" Tag="Details" Click="ViewContextMenu_Click">
                    <MenuItem.Icon><Path Classes="menuIcon iconView" Data="M1.5,3 H4 M5.5,3 H12 M1.5,7 H4 M5.5,7 H12 M1.5,11 H4 M5.5,11 H12" /></MenuItem.Icon>
                  </MenuItem>
                  <MenuItem Header="大图标" Tag="LargeIcons" Click="ViewContextMenu_Click">
                    <MenuItem.Icon><Path Classes="menuIcon iconView" Data="M1.5,1.5 H6 V6 H1.5 Z M8,1.5 H12.5 V6 H8 Z M1.5,8 H6 V12.5 H1.5 Z M8,8 H12.5 V12.5 H8 Z" /></MenuItem.Icon>
                  </MenuItem>
                  <MenuItem Header="超大图标" Tag="ExtraLargeIcons" Click="ViewContextMenu_Click">
                    <MenuItem.Icon><Path Classes="menuIcon iconView" Data="M1.5,1.5 H7 V7 H1.5 Z M8.5,1.5 H12.5 V7 H8.5 Z M1.5,8.5 H7 V12.5 H1.5 Z M8.5,8.5 H12.5 V12.5 H8.5 Z" /></MenuItem.Icon>
                  </MenuItem>
                </MenuFlyout>
              </Button.Flyout>
            </Button>'''
xaml = replace_once(xaml, desktop_old, desktop_new, "desktop view-mode toolbar")

# Mobile: replace the three buttons with one button that opens the same style of overlay as Sort.
mobile_old = '''              <Button Classes="viewMode" ToolTip.Tip="详细信息" Tag="Details" Click="ViewModeButton_Click"><Path Classes="toolbarIcon iconView opticalUp" Data="M1.5,3 H4 M5.5,3 H12 M1.5,7 H4 M5.5,7 H12 M1.5,11 H4 M5.5,11 H12" /></Button>
              <Button Classes="viewMode" ToolTip.Tip="大图标" Tag="LargeIcons" Click="ViewModeButton_Click"><Path Classes="toolbarIcon iconView" Data="M1.5,1.5 H6 V6 H1.5 Z M8,1.5 H12.5 V6 H8 Z M1.5,8 H6 V12.5 H1.5 Z M8,8 H12.5 V12.5 H8 Z" /></Button>
              <Button Classes="viewMode" ToolTip.Tip="超大图标" Tag="ExtraLargeIcons" Click="ViewModeButton_Click"><Path Classes="toolbarIcon iconView" Data="M1.5,1.5 H7 V7 H1.5 Z M8.5,1.5 H12.5 V7 H8.5 Z M1.5,8.5 H7 V12.5 H1.5 Z M8.5,8.5 H12.5 V12.5 H8.5 Z" /></Button>'''
mobile_new = '''              <Button Classes="viewMode" ToolTip.Tip="查看方式" Click="MobileViewModeButton_Click">
                <Path Classes="toolbarIcon iconView opticalUp" Data="M1.5,3 H4 M5.5,3 H12 M1.5,7 H4 M5.5,7 H12 M1.5,11 H4 M5.5,11 H12" />
              </Button>'''
xaml = replace_once(xaml, mobile_old, mobile_new, "mobile view-mode toolbar")

# Add a mobile view-mode action overlay immediately before the existing mobile sort overlay.
sort_marker = '''    <!-- Mobile sort action panel. It intentionally mirrors the image-preview long-press surface. -->'''
view_overlay = '''    <!-- Mobile view-mode action panel. It intentionally mirrors the mobile sort surface. -->
    <Grid x:Name="MobileViewModeActionsOverlay"
          IsVisible="False"
          Background="#72000000"
          ZIndex="3200"
          PointerPressed="MobileViewModeActionsBackdrop_PointerPressed">
      <Border MaxWidth="420" Margin="28"
              HorizontalAlignment="Stretch" VerticalAlignment="Center"
              Padding="0,8" CornerRadius="8"
              Background="#F0444444"
              BorderBrush="#28FFFFFF" BorderThickness="1">
        <StackPanel HorizontalAlignment="Stretch">
          <Button Content="详细信息" Tag="Details"
                  Height="58" Padding="20,0" HorizontalAlignment="Stretch"
                  HorizontalContentAlignment="Left" VerticalContentAlignment="Center"
                  FontSize="18" Foreground="White" Background="Transparent" BorderThickness="0"
                  Click="MobileViewModeAction_Click" />
          <Button Content="大图标" Tag="LargeIcons"
                  Height="58" Padding="20,0" HorizontalAlignment="Stretch"
                  HorizontalContentAlignment="Left" VerticalContentAlignment="Center"
                  FontSize="18" Foreground="White" Background="Transparent" BorderThickness="0"
                  Click="MobileViewModeAction_Click" />
          <Button Content="超大图标" Tag="ExtraLargeIcons"
                  Height="58" Padding="20,0" HorizontalAlignment="Stretch"
                  HorizontalContentAlignment="Left" VerticalContentAlignment="Center"
                  FontSize="18" Foreground="White" Background="Transparent" BorderThickness="0"
                  Click="MobileViewModeAction_Click" />
        </StackPanel>
      </Border>
    </Grid>

'''
xaml = replace_once(xaml, sort_marker, view_overlay + sort_marker, "mobile view-mode overlay")
XAML.write_text(xaml, encoding="utf-8")

cs = CS.read_text(encoding="utf-8")

# NativeControlHost must be hidden while the Avalonia overlay is open, just like the sort overlay.
visibility_old = '''            vm.IsPreviewVisible ||
            MobileSortActionsOverlay.IsVisible ||
            MobilePreviewActionsOverlay.IsVisible ||'''
visibility_new = '''            vm.IsPreviewVisible ||
            MobileViewModeActionsOverlay.IsVisible ||
            MobileSortActionsOverlay.IsVisible ||
            MobilePreviewActionsOverlay.IsVisible ||'''
cs = replace_once(cs, visibility_old, visibility_new, "native overlay visibility guard")

# Android/iOS back closes the view-mode overlay before navigating away.
back_old = '''        // Transient mobile action surfaces are dismissed before page navigation.
        if (MobileSortActionsOverlay.IsVisible)
        {
            CloseMobileSortActions();
            e.Handled = true;
            return;
        }'''
back_new = '''        // Transient mobile action surfaces are dismissed before page navigation.
        if (MobileViewModeActionsOverlay.IsVisible)
        {
            CloseMobileViewModeActions();
            e.Handled = true;
            return;
        }

        if (MobileSortActionsOverlay.IsVisible)
        {
            CloseMobileSortActions();
            e.Handled = true;
            return;
        }'''
cs = replace_once(cs, back_old, back_new, "mobile back handling")

# Opening Sort closes View Mode so only one action surface can exist at a time.
sort_open_old = '''        CancelMobileLongPress();
        CloseMobilePreviewActions();
        MobileSortActionsOverlay.IsVisible = true;'''
sort_open_new = '''        CancelMobileLongPress();
        CloseMobileViewModeActions();
        CloseMobilePreviewActions();
        MobileSortActionsOverlay.IsVisible = true;'''
cs = replace_once(cs, sort_open_old, sort_open_new, "sort overlay mutual exclusion")

# Insert view-mode overlay handlers immediately before the sort-button handler.
sort_handler_marker = '''    private void MobileSortButton_Click(object? sender, RoutedEventArgs e)
    {'''
view_handlers = '''    private void MobileViewModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;

        CancelMobileLongPress();
        CloseMobileSortActions();
        CloseMobilePreviewActions();
        MobileViewModeActionsOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
        e.Handled = true;
    }

    private void CloseMobileViewModeActions()
    {
        MobileViewModeActionsOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
    }

    private void MobileViewModeActionsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseMobileViewModeActions();
        e.Handled = true;
    }

    private async void MobileViewModeAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || DataContext is not MainViewModel vm ||
            !Enum.TryParse<FileViewMode>(tag, out var mode))
            return;

        CloseMobileViewModeActions();
        ClearListSelections();
        await vm.SetViewModeAsync(mode);
        Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
        if (IsMobilePlatform && !UsesNativeMobileFileList)
            Dispatcher.UIThread.Post(UpdateResponsiveMobileIconLayouts, DispatcherPriority.Loaded);
        if (UsesNativeMobileFileList)
            Dispatcher.UIThread.Post(() => UpdateNativeMobileFileListGeometry(vm), DispatcherPriority.Loaded);
        e.Handled = true;
    }

'''
cs = replace_once(cs, sort_handler_marker, view_handlers + sort_handler_marker, "mobile view-mode handlers")

CS.write_text(cs, encoding="utf-8")

print("Combined view-mode UI patch applied.")
