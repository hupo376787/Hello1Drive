namespace Hello1Drive.Services;

public interface IPlatformShareService
{
    Task ShareTextAsync(string title, string text, CancellationToken cancellationToken = default);
}
