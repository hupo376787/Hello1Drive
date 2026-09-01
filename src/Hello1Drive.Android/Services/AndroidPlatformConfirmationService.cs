using Android.App;
using Android.Content;
using Hello1Drive.Services;

namespace Hello1Drive.Android.Services;

public sealed class AndroidPlatformConfirmationService : IPlatformConfirmationService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消")
    {
        var activity = global::Hello1Drive.Android.MainActivity.Instance;
        if (activity is null || activity.IsFinishing)
            return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            if (activity.IsFinishing)
            {
                completion.TrySetResult(false);
                return;
            }

            var dialog = new AlertDialog.Builder(activity)
                .SetTitle(title)
                .SetMessage(message)
                .SetNegativeButton(cancelText, (_, _) => completion.TrySetResult(false))
                .SetPositiveButton(confirmText, (_, _) => completion.TrySetResult(true))
                .Create();

            if (dialog is null)
            {
                completion.TrySetResult(false);
                return;
            }

            // Back/outside-tap dismissal is equivalent to Cancel. The positive/negative button
            // handlers win the race first, so this cannot overwrite an accepted result.
            dialog.SetOnCancelListener(new CancelListener(() => completion.TrySetResult(false)));
            dialog.Show();
        });

        return completion.Task;
    }

    private sealed class CancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }
}
