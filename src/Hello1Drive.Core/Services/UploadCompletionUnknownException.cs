namespace Hello1Drive.Services;

public sealed class UploadCompletionUnknownException() : InvalidOperationException(
    "文件已发送，但暂时无法核实 OneDrive 上的完整内容。请先刷新目标文件夹检查结果，再决定是否重试，避免产生重复文件。");
