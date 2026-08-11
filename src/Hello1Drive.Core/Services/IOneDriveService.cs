using Hello1Drive.Models;

namespace Hello1Drive.Services;

public interface IOneDriveService
{
    long? UploadBytesPerSecondLimit { get; set; }
    long? DownloadBytesPerSecondLimit { get; set; }
    Task<GraphUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<DriveInfoModel> GetDriveInfoAsync(CancellationToken cancellationToken = default);
    Task<byte[]?> GetProfilePhotoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriveItemModel>> GetChildrenAsync(string? parentItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriveItemModel>> GetChildFoldersAsync(string? parentItemId, CancellationToken cancellationToken = default);
    Task<DriveItemPage> GetChildrenPageAsync(
        string? parentItemId,
        string? nextLink = null,
        int pageSize = 120,
        CancellationToken cancellationToken = default,
        string? orderBy = null);
    Task<DriveItemModel> GetItemMetadataAsync(string? itemId, CancellationToken cancellationToken = default);
    Task<byte[]?> GetThumbnailAsync(DriveItemModel item, CancellationToken cancellationToken = default);
    Task<DriveItemModel> CreateFolderAsync(string? parentItemId, string name, CancellationToken cancellationToken = default);
    Task<DriveItemModel> RenameAsync(string itemId, string newName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string itemId, CancellationToken cancellationToken = default);
    Task MoveAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default);
    Task CopyAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default);
    Task<string> CreateShareLinkAsync(string itemId, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string? parentItemId, string fileName, Stream source, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task UpdateFileContentAsync(string itemId, Stream source, string contentType = "text/plain; charset=utf-8", CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string itemId, Stream destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken cancellationToken = default);
}
