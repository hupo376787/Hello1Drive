# Loading, splash and folder navigation

- Startup shows a short cross-platform Hello1Drive splash. Initialization starts immediately behind it; cached startup content is not forced to wait for Graph synchronization before the splash fades.
- Every indeterminate loading state uses `LoadingIndicator`: circular activity ring above `加载中`, with a transparent loading surface.
- Folder navigation cancels thumbnail work and any pending load-more request from the folder being left.
- Cached folders open without showing the global busy indicator.
- A remote folder shows its first `children` page as soon as that request completes. Folder `childCount` metadata is no longer awaited on the critical path; when it is not already known from the parent item it is refreshed in the background.
- Android root Back requests a compact close confirmation. Android closes through its Activity lifecycle (`FinishAffinity`) rather than process termination from shared code.
- The Settings action `下载所有 OneDrive 文件` uses a compact confirmation dialog. The expensive recursive planning stage then uses the same unified loading indicator.
