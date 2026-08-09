namespace Hello1Drive.Configuration;

public static class AppConfig
{
    // Microsoft Entra application (client) ID. Client IDs are public identifiers, not secrets.
    public const string ClientId = "9ea6a8b7-0122-4c9a-8b14-752d60de9626";

    public const string Authority = "https://login.microsoftonline.com/consumers";
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    public static readonly string[] GraphScopes =
    [
        "User.Read",
        "Files.ReadWrite"
    ];

    public const string DesktopRedirectUri = "http://localhost";
    public const string AndroidPackageId = "com.xiaowei.hello1drive";
    public const string AndroidRedirectUri = "msal9ea6a8b7-0122-4c9a-8b14-752d60de9626://auth";
    public const string IosBundleId = "com.xiaowei.hello1drive";
    public const string IosRedirectUri = "msauth.com.xiaowei.hello1drive://auth";
}
