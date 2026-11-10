namespace Hello1Drive.Services;

// OneDrive's 160-bit QuickXor content checksum (not a cryptographic authentication hash).
// Algorithm: https://learn.microsoft.com/onedrive/developer/code-snippets/quickxorhash
internal sealed class OneDriveContentHash
{
    private readonly byte[] _bytes = new byte[20];
    private int _shift;
    private long _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            var index = _shift / 8;
            var bits = _shift % 8;
            _bytes[index] ^= (byte)(value << bits);
            if (bits != 0)
                _bytes[(index + 1) % _bytes.Length] ^= (byte)(value >> (8 - bits));
            _shift = (_shift + 11) % 160;
        }
        _length += data.Length;
    }

    public string GetValue()
    {
        var result = (byte[])_bytes.Clone();
        for (var i = 0; i < 8; i++)
            result[12 + i] ^= (byte)((ulong)_length >> (i * 8));
        return Convert.ToBase64String(result);
    }

    public static async Task<string> ComputeAsync(Stream source, long position, CancellationToken token)
    {
        var original = source.Position;
        try
        {
            source.Position = position;
            var hash = new OneDriveContentHash();
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                hash.Append(buffer.AsSpan(0, count));
            return hash.GetValue();
        }
        finally { source.Position = original; }
    }
}
