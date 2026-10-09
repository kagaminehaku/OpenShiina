// The one activity: landscape either way up (the games are wider than they are tall), kept as
// it is when the screen turns or a keyboard comes (no restart, which would lose the game), and
// telling the player when it goes to the background and comes back (the game is no longer in
// front: it pauses its music, as on the desktop) and the answers to the permissions it asks. The
// volume buttons set the media volume.

using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using OpenShiina.App;

namespace OpenShiina.Android;

[Activity(
    Label = "OpenShiina",
    Theme = "@style/OpenShiinaTheme",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize |
        ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden |
        ConfigChanges.Navigation | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <summary>The activity on screen (permissions are asked through it).</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        // The volume buttons change the media volume (the game's sound), also between sounds
        VolumeControlStream = global::Android.Media.Stream.Music;
    }

    protected override void OnResume()
    {
        Current = this;
        base.OnResume();
        PlayerPlatform.SetActive(true);
    }

    protected override void OnPause()
    {
        PlayerPlatform.SetActive(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (Current == this)
            Current = null;
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidStorage.PermissionAnswered(requestCode, grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted));
    }
}
