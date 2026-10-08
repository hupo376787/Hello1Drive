using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private bool _openingTransferFile;

    private static async Task RememberTransferLocalFileAsync(
        MainViewModel vm, TransferItemModel transfer, IStorageFile file)
    {
        var fileUri = file.Path.ToString();
        var bookmark = string.Equals(transfer.LocalFileUri, fileUri, StringComparison.Ordinal)
            ? transfer.LocalFileBookmark
            : null;
        try
        {
            var saved = await file.SaveBookmarkAsync();
            if (!string.IsNullOrWhiteSpace(saved))
                bookmark = saved;
        }
        catch { }
        vm.SetTransferLocalFile(transfer, fileUri, bookmark);
    }

    private async void TransferItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (_openingTransferFile || sender is not Control { DataContext: TransferItemModel transfer } ||
            DataContext is not MainViewModel vm)
            return;

        // Taps on the retry button belong to the button, never to the row's preview action.
        if (e.Source is Visual source &&
            (source is Button || source.GetVisualAncestors().Any(visual => visual is Button)))
            return;

        e.Handled = true;
        if (transfer.Direction != TransferDirection.Upload && transfer.State != TransferState.Completed)
        {
            vm.ErrorMessage = "文件尚未下载或缓存完成，暂时无法预览。";
            return;
        }

        var fileUri = transfer.LocalFileUri;
        var bookmark = transfer.LocalFileBookmark;
        // Older unfinished single-file transfers already have a usable storage bookmark.
        if (string.IsNullOrWhiteSpace(bookmark) &&
            transfer.ResumeInfo is { Kind: TransferResumeKind.UploadFile or TransferResumeKind.DownloadFile } resume)
            bookmark = resume.StorageBookmark;

        if (string.IsNullOrWhiteSpace(fileUri) && string.IsNullOrWhiteSpace(bookmark))
        {
            vm.ErrorMessage = "此传输记录没有本地文件位置，无法预览。";
            return;
        }

        _openingTransferFile = true;
        IStorageFile? file = null;
        try
        {
            vm.ErrorMessage = null;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return;

            Uri.TryCreate(fileUri, UriKind.Absolute, out var uri);
            if (uri?.IsFile == true && !File.Exists(uri.LocalPath))
                throw new FileNotFoundException();

            if (topLevel.StorageProvider is { } provider)
            {
                if (!string.IsNullOrWhiteSpace(bookmark))
                {
                    try { file = await provider.OpenFileBookmarkAsync(bookmark); } catch { }
                }
                if (file is null && uri is not null)
                    file = await provider.TryGetFileFromPathAsync(uri);
            }

            var opened = false;
            if (file is not null)
            {
                // Document-provider handles can survive deletion. Verify that the file is
                // readable before handing it to the system preview/default application.
                await using (var stream = await file.OpenReadAsync()) { }
                opened = await topLevel.Launcher.LaunchFileAsync(file);
            }
            if (!opened && uri?.IsFile == true)
                opened = await TryLaunchSystemFileAsync(uri.LocalPath);

            if (!opened)
            {
                vm.ErrorMessage = file is null && uri?.IsFile != true
                    ? "本地文件不存在，或文件访问权限已失效。"
                    : "无法打开文件，请安装支持此文件类型的应用。";
                return;
            }

            vm.StatusText = $"已打开本地文件：{transfer.FileName}";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            vm.ErrorMessage = "本地文件不存在，可能已被移动或删除。";
        }
        catch (UnauthorizedAccessException)
        {
            vm.ErrorMessage = "没有权限访问此本地文件。";
        }
        catch (Exception)
        {
            vm.ErrorMessage = "无法预览本地文件，请检查文件是否存在以及访问权限。";
        }
        finally
        {
            file?.Dispose();
            _openingTransferFile = false;
        }
    }
}
