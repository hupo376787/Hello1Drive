namespace Hello1Drive.Services;

public interface IDesktopInputSettingsService
{
    int GetMouseWheelScrollLines();
}

public static class DesktopScrollSettings
{
    public const int UseFrameworkDefault = -1;
    public const int ScrollByPage = -2;
}
