// Match native exception identities without needing an Android device/runtime for HTTP tests.
namespace Java.Net
{
    public sealed class SocketException(string message) : Exception(message);
}

namespace Java.IO
{
    public sealed class IOException(string message) : Exception(message);
}
