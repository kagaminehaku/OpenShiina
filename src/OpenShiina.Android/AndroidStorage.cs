// The games' folders on Android. The player reads them as files, as on the desktop, which needs
// all files access from Android 11 (the user turns it on in the settings page the player opens)
// and the storage permission before. The folder picker gives a document tree, not a path: a tree
// of the device's storage ("primary:Games/MyGame"), of a memory card ("1234-ABCD:Games/MyGame")
// or of the Downloads ("raw:/storage/emulated/0/Download/MyGame") is turned into its path.

using Android;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Avalonia.Platform.Storage;
using AndroidUri = Android.Net.Uri;
using AndroidEnvironment = Android.OS.Environment;

namespace OpenShiina.Android;

public static class AndroidStorage
{
    private const int StorageRequest = 0x5348;
    private static TaskCompletionSource<bool>? s_answer;

    /// <summary>True when the player may read the device's files; else asks the user (and false until they allow it).</summary>
    public static Task<bool> EnsureFileAccessAsync()
    {
        var activity = MainActivity.Current;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (AndroidEnvironment.IsExternalStorageManager)
                return Task.FromResult(true);
            if (activity == null)
                return Task.FromResult(false);
            // The settings page of this app's all files access; the user comes back and tries again
            try
            {
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, AndroidUri.Parse($"package:{activity.PackageName}"));
                activity.StartActivity(intent);
            }
            catch (ActivityNotFoundException)
            {
                activity.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }
            return Task.FromResult(false);
        }
        if (activity == null)
            return Task.FromResult(false);
        if (HasSharedAccess(activity))
            return Task.FromResult(true);
        s_answer?.TrySetResult(false);
        s_answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions([Manifest.Permission.ReadExternalStorage, Manifest.Permission.WriteExternalStorage], StorageRequest);
        return s_answer.Task;
    }

    /// <summary>
    /// The player may read and write the device's storage: all files access from Android 11, the
    /// storage permissions before (to read the games, and to keep the player folder there).
    /// </summary>
    public static bool HasSharedAccess(Context context) =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
            ? AndroidEnvironment.IsExternalStorageManager
            : context.CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted
              && context.CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    /// <summary>The player folder on the device's storage, where any file manager sees it: OpenShiina.</summary>
    public static string SharedPlayerFolder =>
        Path.Combine(AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0", "OpenShiina");

    /// <summary>
    /// Shows a folder of the device's storage in Android's Files (its document of the folder,
    /// viewed); false when it is not on the device's storage or no app shows it.
    /// </summary>
    public static bool OpenFolder(string path)
    {
        var activity = MainActivity.Current;
        string storage = AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        string full = Path.GetFullPath(path);
        if (activity == null || !full.StartsWith(storage + "/", StringComparison.Ordinal))
            return false;
        // Folders under Android/data are the apps' own: Files does not show them from Android 11
        string relative = full[(storage.Length + 1)..];
        if (relative.StartsWith("Android/data", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsAndroidVersionAtLeast(30))
            return false;
        try
        {
            var uri = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + relative);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, DocumentsContract.Document.MimeTypeDir);
            // No grant of the URI: the player has none to give (Android refuses the intent then),
            // and Files reads the device's storage itself
            intent.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(intent);
            return true;
        }
        catch (Exception e) when (e is ActivityNotFoundException or Java.Lang.Exception)
        {
            global::Android.Util.Log.Warn("OpenShiina", $"Could not show {path} in Files: {e.Message}");
            return false;
        }
    }

    /// <summary>The user answered the permission the player asked for (MainActivity).</summary>
    public static void PermissionAnswered(int requestCode, bool granted)
    {
        if (requestCode != StorageRequest)
            return;
        s_answer?.TrySetResult(granted);
        s_answer = null;
    }

    /// <summary>The path of a folder the picker gave, or null when it is not on the device's storage.</summary>
    public static string? FolderPath(IStorageFolder folder)
    {
        if (folder.TryGetLocalPath() is { } local && Directory.Exists(local))
            return local;
        try
        {
            var uri = AndroidUri.Parse(folder.Path.OriginalString);
            if (uri == null)
                return null;
            string? id = DocumentsContract.IsTreeUri(uri) ? DocumentsContract.GetTreeDocumentId(uri) : DocumentsContract.GetDocumentId(uri);
            return id == null ? null : PathOf(uri.Authority, id);
        }
        catch (Exception e) when (e is Java.Lang.Exception or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static string? PathOf(string? authority, string id)
    {
        if (id.StartsWith("raw:", StringComparison.Ordinal))
            return id[4..];
        if (authority != "com.android.externalstorage.documents")
            return null;
        int colon = id.IndexOf(':');
        if (colon < 0)
            return null;
        string volume = id[..colon], relative = id[(colon + 1)..];
        string root = volume switch
        {
            "primary" => AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0",
            "home" => Path.Combine(AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0", "Documents"),
            _ => $"/storage/{volume}",
        };
        string path = relative.Length > 0 ? Path.Combine(root, relative) : root;
        return Directory.Exists(path) ? path : null;
    }
}
