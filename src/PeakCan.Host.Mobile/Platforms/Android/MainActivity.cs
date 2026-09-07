using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PeakCan.Host.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionView, Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "application/octet-stream")]
public class MainActivity : MauiAppCompatActivity
{
    public static Android.Net.Uri? PendingFileUri { get; private set; }

    public static Android.Net.Uri? TakePendingFileUri()
    {
        var uri = PendingFileUri;
        PendingFileUri = null;
        return uri;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        PendingFileUri = ExtractTraceUri(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        PendingFileUri = ExtractTraceUri(intent);
    }

    private static Android.Net.Uri? ExtractTraceUri(Intent? intent)
    {
        if (intent?.Data is not null) return intent.Data;
        if (intent?.Action != Intent.ActionSend) return null;

        // Android 13 introduces the typed overload; Android 12 uses the legacy API.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;

#pragma warning disable CA1416 // Only reached below API 33.
        return intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
#pragma warning restore CA1416
    }
}
