// Android's part of the updates (AppUpdater, TranslationService): whether the network is charged
// by the amount (ConnectivityManager: mobile data, a phone's hotspot), and a new release's APK given
// to the system's installer, which asks the user. Installing from OpenShiina has to be allowed once
// (Android 8 and later: the settings page this opens); the APK reaches the installer through the
// app's FileProvider (Resources/xml/file_paths.xml), as an APK on the device's storage can.

using Android.Content;
using Android.Net;
using Android.Provider;
using AndroidX.Core.Content;
using AndroidUri = Android.Net.Uri;

namespace OpenShiina.Android;

public static class AndroidUpdates
{
    /// <summary>The network in use is charged by the amount (true too when it cannot be told).</summary>
    public static bool MeteredNetwork() =>
        (global::Android.App.Application.Context.GetSystemService(Context.ConnectivityService) as ConnectivityManager)?.IsActiveNetworkMetered ?? true;

    /// <summary>The APK at <paramref name="path"/> to the system's installer; false when OpenShiina may not install yet (the page to allow it opens).</summary>
    public static bool InstallPackage(string path)
    {
        var activity = MainActivity.Current;
        if (activity?.PackageManager == null)
            return false;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !activity.PackageManager.CanRequestPackageInstalls())
        {
            try
            {
                activity.StartActivity(new Intent(Settings.ActionManageUnknownAppSources, AndroidUri.Parse($"package:{activity.PackageName}")));
            }
            catch (ActivityNotFoundException)
            {
            }
            return false;
        }
        var uri = FileProvider.GetUriForFile(activity, activity.PackageName + ".files", new Java.IO.File(path));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
        return true;
    }
}
