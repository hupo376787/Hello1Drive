namespace Hello1Drive.Services;

/// <summary>
/// Raised when a OneDrive backend rejects an otherwise documented $orderby field.
/// Some consumer storage backends currently reject size ordering with the internal
/// SMTotalFileStreamSize property even though OneDrive documents size as sortable.
/// </summary>
public sealed class GraphOrderByNotSupportedException(string field, string detail)
    : Exception($"当前 OneDrive 后端不支持按 {field} 排序。\n{detail}")
{
    public string Field { get; } = field;
}
