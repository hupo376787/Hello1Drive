using System.Net;

namespace Hello1Drive.Services;

internal sealed class UploadSessionExpiredException() : HttpRequestException(
    "OneDrive 上传会话已结束或失效，正在核对上传结果。", null, HttpStatusCode.NotFound);
