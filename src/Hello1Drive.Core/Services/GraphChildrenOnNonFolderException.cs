namespace Hello1Drive.Services;

/// <summary>
/// Raised when Graph rejects /children because the selected drive item is not an
/// enumerable folder. OneDrive Personal Vault can surface this way on some consumer
/// backends while locked.
/// </summary>
public sealed class GraphChildrenOnNonFolderException(string detail)
    : Exception("当前项目不能通过 Microsoft Graph 按普通文件夹读取。", null)
{
    public string Detail { get; } = detail;
}
