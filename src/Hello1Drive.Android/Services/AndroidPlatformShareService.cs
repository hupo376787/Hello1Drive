using Android.Content;
using Hello1Drive.Services;

namespace Hello1Drive.Android.Services;

public sealed class AndroidPlatformShareService : IPlatformShareService
{
    public Task ShareTextAsync(string title, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activity = global::Hello1Drive.Android.MainActivity.Instance
            ?? throw new InvalidOperationException("Android Activity 尚未初始化。");

        var shareIntent = new Intent(Intent.ActionSend);
        shareIntent.SetType("text/plain");
        shareIntent.PutExtra(Intent.ExtraSubject, title);
        shareIntent.PutExtra(Intent.ExtraText, text);
        activity.StartActivity(Intent.CreateChooser(shareIntent, title));
        return Task.CompletedTask;
    }
}
