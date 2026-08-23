# Loading, splash and folder navigation

- Startup shows a short cross-platform Hello1Drive splash. Initialization starts immediately behind it; cached startup content is not forced to wait for Graph synchronization before the splash fades.
- Every indeterminate loading state uses `LoadingIndicator`: circular activity ring above `加载中`, with a transparent loading surface.
- Folder navigation cancels thumbnail work and the background metadata enumerator from the folder being left. There is no scroll-triggered load-more request on mobile.
- Cached folders open without showing the global busy indicator.
- A remote folder shows its first `children` page as soon as that request completes. Known `folder.childCount` creates the complete mobile logical slot range immediately; when the count is unknown, its metadata request runs in parallel with page one and reconciles the placeholder tail without delaying first paint. Remaining `children` pages enumerate in the background independently of scrolling.
- Android root Back requests a compact close confirmation. Android closes through its Activity lifecycle (`FinishAffinity`) rather than process termination from shared code.
- The Settings action `下载所有 OneDrive 文件` uses a compact confirmation dialog. The expensive recursive planning stage then uses the same unified loading indicator.
