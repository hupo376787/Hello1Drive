using System.Net;
using System.Net.Sockets;

namespace Hello1Drive.Services;

internal static class NetworkFailureClassifier
{
    public static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        // A full drive won't recover by replaying the same request indefinitely.
        return code is 408 or 425 or 429 || (code >= 500 && status != HttpStatusCode.InsufficientStorage);
    }

    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        if (exception is OperationCanceledException or TimeoutException or SocketException)
            return true;
        if (exception is HttpRequestException http)
            return http.StatusCode is null || IsTransientStatus(http.StatusCode.Value);

        // Android's handler can expose Java.IO.IOException/Java.Net.SocketException directly,
        // without an HttpRequestException wrapper. Keep Core independent from Mono.Android and
        // only retry recognized socket/network failures, never arbitrary local file I/O errors.
        var typeName = exception.GetType().FullName ?? string.Empty;
        var nativeIo = false;
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
            nativeIo |= type.FullName == "Java.IO.IOException";
        if (exception is IOException || typeName.StartsWith("Java.Net.", StringComparison.Ordinal) || nativeIo)
        {
            var message = exception.Message;
            if (new[] { "socket closed", "socket is closed", "software caused connection abort",
                    "connection reset", "connection aborted", "connection abort", "broken pipe",
                    "network is unreachable", "no route to host", "unexpected end of stream" }
                .Any(text => message.Contains(text, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (typeName is "Java.Net.SocketException" or "Java.Net.SocketTimeoutException" or
                "Java.Net.UnknownHostException" or "Java.Net.ConnectException" or "Java.Net.NoRouteToHostException")
                return true;
        }

        return exception.InnerException is not null && IsTransient(exception.InnerException, cancellationToken);
    }
}
