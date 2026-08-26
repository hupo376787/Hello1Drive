using System.Collections.Concurrent;
using Android.Graphics.Drawables;
using Android.Views;
using Avalonia.Android;
using Avalonia.Platform;
using AndroidX.RecyclerView.Widget;
using Hello1Drive.Controls;

namespace Hello1Drive.Android.Services;

/// <summary>
/// Small Android-only decorator around the native file-list factory.
///
/// The core RecyclerView implementation deliberately avoids thumbnail/UI work while a fling is
/// active. Its idle prefetch already warms the current, previous and next viewports, but RecyclerView
/// may re-attach a cached ViewHolder without calling OnBindViewHolder again. A holder that was bound
/// during the fling can therefore keep drawing its old placeholder even after the corresponding
/// bitmap has been prefetched into the adapter cache.
///
/// This decorator fixes that RecyclerView cache edge case without touching the hot scrolling path:
/// when scrolling becomes idle it marks only the two adjacent viewport-sized ranges as changed.
/// Cached holders in those ranges are then rebound before they are shown again and immediately pick
/// up the bitmap that the existing native prefetch pipeline placed in memory/disk cache.
/// </summary>
internal sealed class PolishedAndroidNativeMobileFileListFactory : INativeMobileFileListFactory
{
    private readonly AndroidNativeMobileFileListFactory _inner = new();
    private readonly ConcurrentDictionary<nint, NativeFileSurfacePolishSession> _sessions = new();

    public IPlatformHandle CreateControl(IPlatformHandle parent, NativeMobileFileListHost host)
    {
        var control = _inner.CreateControl(parent, host);
        if (control is AndroidViewControlHandle androidHandle)
        {
            var session = NativeFileSurfacePolishSession.Attach(androidHandle.View);
            if (session is not null)
                _sessions[control.Handle] = session;
        }

        return control;
    }

    public void DestroyControl(IPlatformHandle control)
    {
        if (_sessions.TryRemove(control.Handle, out var session))
            session.Dispose();

        _inner.DestroyControl(control);
    }
}

internal sealed class NativeFileSurfacePolishSession : IDisposable
{
    private readonly RecyclerView? _recycler;
    private readonly AdjacentViewportThumbnailRecoveryListener? _thumbnailRecoveryListener;
    private readonly NativeFloatingUploadButtonView? _floatingUpload;
    private readonly Drawable? _floatingUploadForeground;
    private bool _disposed;

    private NativeFileSurfacePolishSession(
        RecyclerView? recycler,
        AdjacentViewportThumbnailRecoveryListener? thumbnailRecoveryListener,
        NativeFloatingUploadButtonView? floatingUpload,
        Drawable? floatingUploadForeground)
    {
        _recycler = recycler;
        _thumbnailRecoveryListener = thumbnailRecoveryListener;
        _floatingUpload = floatingUpload;
        _floatingUploadForeground = floatingUploadForeground;
    }

    public static NativeFileSurfacePolishSession? Attach(View root)
    {
        var recycler = FindDescendant<RecyclerView>(root);
        var floatingUpload = FindDescendant<NativeFloatingUploadButtonView>(root);

        AdjacentViewportThumbnailRecoveryListener? listener = null;
        if (recycler is not null)
        {
            listener = new AdjacentViewportThumbnailRecoveryListener();
            recycler.AddOnScrollListener(listener);
        }

        Drawable? foreground = null;
        if (floatingUpload is not null)
        {
            // The native FAB already owns hit-testing, drag persistence, elevation and clipping.
            // A vector foreground changes only its artwork and completely covers the legacy
            // arrow/tray drawing underneath, so no additional View is introduced.
            foreground = floatingUpload.Context.GetDrawable(
                global::Hello1Drive.Android.Resource.Drawable.fab_cloud_upload);
            if (foreground is not null)
            {
                floatingUpload.Foreground = foreground;
                floatingUpload.Invalidate();
            }
        }

        if (recycler is null && floatingUpload is null)
            return null;

        return new NativeFileSurfacePolishSession(recycler, listener, floatingUpload, foreground);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_recycler is not null && _thumbnailRecoveryListener is not null)
            _recycler.RemoveOnScrollListener(_thumbnailRecoveryListener);

        if (_floatingUpload is not null && ReferenceEquals(_floatingUpload.Foreground, _floatingUploadForeground))
            _floatingUpload.Foreground = null;

        _thumbnailRecoveryListener?.Dispose();
        _floatingUploadForeground?.Dispose();
    }

    private static T? FindDescendant<T>(View view) where T : View
    {
        if (view is T match)
            return match;

        if (view is not ViewGroup group)
            return null;

        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child is null)
                continue;

            var nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private sealed class AdjacentViewportThumbnailRecoveryListener : RecyclerView.OnScrollListener
    {
        private int _lastVerticalDirection = 1;

        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            base.OnScrolled(recyclerView, dx, dy);
            if (dy > 0)
                _lastVerticalDirection = 1;
            else if (dy < 0)
                _lastVerticalDirection = -1;
        }

        public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
        {
            base.OnScrollStateChanged(recyclerView, newState);
            if (newState != RecyclerView.ScrollStateIdle)
                return;

            // Never notify the adapter from inside RecyclerView's own scroll-state callback. Post
            // one small idle task so any final layout/prefetch bookkeeping can finish first.
            recyclerView.Post(() =>
            {
                if (recyclerView.ScrollState != RecyclerView.ScrollStateIdle ||
                    recyclerView.GetAdapter() is not RecyclerView.Adapter adapter ||
                    recyclerView.GetLayoutManager() is not LinearLayoutManager layout ||
                    adapter.ItemCount <= 0)
                {
                    return;
                }

                var first = layout.FindFirstVisibleItemPosition();
                var last = layout.FindLastVisibleItemPosition();
                if (first < 0 || last < first)
                    return;

                var viewportItems = Math.Max(1, last - first + 1);

                // Mark the viewport the user just passed first, then the opposite look-ahead
                // viewport. The existing adapter still owns all thumbnail download/decode work;
                // these notifications only invalidate stale cached holders and are O(screen size).
                if (_lastVerticalDirection >= 0)
                {
                    MarkRange(adapter, first - viewportItems, first - 1);
                    MarkRange(adapter, last + 1, last + viewportItems);
                }
                else
                {
                    MarkRange(adapter, last + 1, last + viewportItems);
                    MarkRange(adapter, first - viewportItems, first - 1);
                }
            });
        }

        private static void MarkRange(RecyclerView.Adapter adapter, int requestedFrom, int requestedTo)
        {
            var itemCount = adapter.ItemCount;
            if (itemCount <= 0)
                return;

            var from = Math.Clamp(requestedFrom, 0, itemCount - 1);
            var to = Math.Clamp(requestedTo, 0, itemCount - 1);
            if (requestedTo < 0 || requestedFrom >= itemCount || to < from)
                return;

            adapter.NotifyItemRangeChanged(from, to - from + 1);
        }
    }
}
