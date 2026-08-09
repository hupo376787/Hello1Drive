using System.Collections.Concurrent;
using System.Text;
using Pinyin.NET;

namespace Hello1Drive.Services;

/// <summary>
/// Explorer/Start-menu-like mixed Chinese/Latin name comparer.
/// Chinese text participates in the same alphabetic sequence by comparing a no-tone pinyin key.
/// Example: Account, 哔哩哔哩(bilibili), 图片(tupian), Yolo.
/// </summary>
public sealed class PinyinNameComparer : IComparer<string?>
{
    public static readonly PinyinNameComparer Instance = new();

    private static readonly PinyinProcessor Processor = new(PinyinFormat.WithoutTone);
    private static readonly ConcurrentDictionary<string, string> KeyCache = new(StringComparer.Ordinal);
    private static readonly object ProcessorGate = new();

    private PinyinNameComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var keyX = KeyCache.GetOrAdd(x, BuildKey);
        var keyY = KeyCache.GetOrAdd(y, BuildKey);

        var result = StringComparer.CurrentCultureIgnoreCase.Compare(keyX, keyY);
        return result != 0
            ? result
            : StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }

    private static string BuildKey(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        try
        {
            // PinyinM.NET handles mixed Chinese/English text.  Keep only the first reading for
            // deterministic file ordering; ambiguous readings still get a stable fallback below.
            // The processor is guarded because sort-key generation can also happen while a page is appended.
            var builder = new StringBuilder(value.Length * 2);
            lock (ProcessorGate)
            {
                var item = Processor.GetPinyin(value);
                if (item is null)
                    return value;

                // PinyinM.NET's nullable annotations do not let the compiler carry the
                // preceding null check through to the Keys access in all target frameworks.
                // The null-forgiving operator is safe here because item was checked above.
                foreach (var alternatives in item!.Keys!)
                {
                    var first = alternatives?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(first))
                        builder.Append(first);
                }
            }

            return builder.Length > 0 ? builder.ToString() : value;
        }
        catch
        {
            // A name must never make the file list unsortable. Unknown characters simply use
            // the platform's culture-aware comparison as the fallback key.
            return value;
        }
    }
}
