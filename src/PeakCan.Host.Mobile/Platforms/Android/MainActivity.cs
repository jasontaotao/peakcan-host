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
    DataMimeType = "*/*")]
public class MainActivity : MauiAppCompatActivity
{
    public static Android.Net.Uri? PendingFileUri { get; private set; }

    /// <summary>Fired when a VIEW/SEND intent arrives while the app is already running.</summary>
    public static event Action<Android.Net.Uri>? FileUriReceived;

    public static Android.Net.Uri? TakePendingFileUri()
    {
        var uri = PendingFileUri;
        PendingFileUri = null;
        return uri;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Must extract BEFORE base.OnCreate: it triggers MAUI page creation
        // and FilesPage.OnAppearing consumes PendingFileUri.
        PendingFileUri = ExtractTraceUri(Intent);
        base.OnCreate(savedInstanceState);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        PendingFileUri = ExtractTraceUri(intent);
        if (PendingFileUri is not null) FileUriReceived?.Invoke(PendingFileUri);
    }

    private static Android.Net.Uri? ExtractTraceUri(Intent? intent)
    {
        if (intent?.Data is not null) return intent.Data;
        if (intent?.Action != Intent.ActionSend) return null;

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;

#pragma warning disable CA1416
        return intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
#pragma warning restore CA1416
    }
}

