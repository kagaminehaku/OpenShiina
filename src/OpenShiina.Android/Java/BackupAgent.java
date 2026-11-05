// Android's backup of the app (Google's cloud, a new phone's transfer): the player folder (the
// app's folder on the shared storage; OpenShiina on the device's storage when the player keeps it
// there, its files copied into the app's folder first), nothing else (the app's own folder holds
// only the schemes, copied out at every start). Nothing when Settings > Back up saves is off ("AndroidBackup" in
// settings.json). Left out: the logs (*.log, also those in the save folders: perf.log,
// draw-trace.log, crash.log, stall.log), the library and last-game.txt (folders of this device).
// Android keeps 25 MB of an app (FullBackupDataOutput.getQuota) and keeps nothing when a backup
// is larger: the files of the player folder itself go first, then each game's save folder
// (scn/<game>) whole, the one played last first, as long as it fits; the others are left out.
// In Java: Android runs a backup agent without the app's own start, so without .NET.

package com.kagaminehaku.openshiina;

import android.app.backup.BackupDataInput;
import android.app.backup.BackupDataOutput;
import android.app.backup.FullBackupDataOutput;
import android.os.ParcelFileDescriptor;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import org.json.JSONObject;

public class BackupAgent extends android.app.backup.BackupAgent
{
    // What a file takes in the backup besides its bytes (a tar header, and its last block filled
    // out), and what is kept back from the quota for the rest
    private static final long HEADER = 1024;
    private static final long MARGIN = 256 * 1024;

    @Override
    public void onFullBackup(FullBackupDataOutput data) throws IOException
    {
        File root = getExternalFilesDir(null);
        if (root == null)
            return;
        // The player folder is OpenShiina on the device's storage once the player may write there
        // (MainApplication.PlayerRoot); Android backs up only the app's own folders, so its files
        // are copied into the app's folder first (those newer there), and that one backed up
        File shared = new File(android.os.Environment.getExternalStorageDirectory(), "OpenShiina");
        if (shared.isDirectory() && shared.canRead())
            mirror(shared, root, true);
        if (!backupOn(root))
            return;
        long left = data.getQuota() - MARGIN;
        // the player folder's own files (settings.json), then its folders by the time of their
        // newest file, the save folders of the games one by one
        List<File> files = new ArrayList<>();
        List<File> folders = new ArrayList<>();
        File[] children = root.listFiles();
        if (children == null)
            return;
        for (File child : children)
        {
            if (child.isFile())
            {
                if (wanted(child, true))
                    files.add(child);
            }
            else if (child.isDirectory() && child.getName().equals("scn"))
            {
                File[] games = child.listFiles();
                if (games != null)
                    for (File game : games)
                        if (game.isDirectory())
                            folders.add(game);
            }
            else if (child.isDirectory())
                folders.add(child);
        }
        for (File file : files)
        {
            long size = cost(file);
            if (size <= left)
            {
                fullBackupFile(file, data);
                left -= size;
            }
        }
        folders.sort((a, b) -> Long.compare(newest(b), newest(a)));
        for (File folder : folders)
        {
            List<File> inside = new ArrayList<>();
            collect(folder, inside);
            long size = 0;
            for (File file : inside)
                size += cost(file);
            if (size > left)
                continue;
            for (File file : inside)
                fullBackupFile(file, data);
            left -= size;
        }
    }

    /** Copies the files of a folder that go into the backup to another, where they are missing or older. */
    private static void mirror(File from, File to, boolean inRoot)
    {
        File[] children = from.listFiles();
        if (children == null)
            return;
        for (File child : children)
        {
            File target = new File(to, child.getName());
            if (child.isDirectory())
            {
                if (target.isDirectory() || target.mkdirs())
                    mirror(child, target, false);
            }
            else if (child.isFile() && wanted(child, inRoot)
                     && (!target.isFile() || target.lastModified() < child.lastModified() || target.length() != child.length()))
            {
                try (FileInputStream in = new FileInputStream(child); java.io.FileOutputStream out = new java.io.FileOutputStream(target))
                {
                    byte[] buffer = new byte[1 << 16];
                    for (int n; (n = in.read(buffer)) > 0; )
                        out.write(buffer, 0, n);
                }
                catch (IOException e)
                {
                    continue;
                }
                target.setLastModified(child.lastModified());
            }
        }
    }

    /** Whether a file goes into the backup: not a log, and not the library (in the player folder itself). */
    private static boolean wanted(File file, boolean inRoot)
    {
        String name = file.getName().toLowerCase(Locale.ROOT);
        if (name.endsWith(".log"))
            return false;
        return !(inRoot && (name.equals("library.json") || name.equals("last-game.txt")));
    }

    private static long cost(File file)
    {
        return (file.length() + 511) / 512 * 512 + HEADER;
    }

    private static void collect(File folder, List<File> into)
    {
        File[] children = folder.listFiles();
        if (children == null)
            return;
        for (File child : children)
        {
            if (child.isDirectory())
                collect(child, into);
            else if (child.isFile() && wanted(child, false))
                into.add(child);
        }
    }

    /** The time of the newest file in a folder (the game played last writes its saves). */
    private static long newest(File folder)
    {
        long time = 0;
        File[] children = folder.listFiles();
        if (children == null)
            return 0;
        for (File child : children)
            time = Math.max(time, child.isDirectory() ? newest(child) : child.lastModified());
        return time;
    }

    /** Settings > Back up saves: on unless settings.json says false (also when it cannot be read). */
    private static boolean backupOn(File root)
    {
        File settings = new File(root, "settings.json");
        if (!settings.isFile())
            return true;
        try (FileInputStream stream = new FileInputStream(settings))
        {
            byte[] bytes = new byte[(int) Math.min(settings.length(), 1 << 20)];
            int n = 0;
            while (n < bytes.length)
            {
                int read = stream.read(bytes, n, bytes.length - n);
                if (read < 0)
                    break;
                n += read;
            }
            return new JSONObject(new String(bytes, 0, n, StandardCharsets.UTF_8)).optBoolean("AndroidBackup", true);
        }
        catch (Exception e)
        {
            return true;
        }
    }

    // The key-value backup is not used (the manifest's fullBackupOnly)
    @Override
    public void onBackup(ParcelFileDescriptor oldState, BackupDataOutput data, ParcelFileDescriptor newState)
    {
    }

    @Override
    public void onRestore(BackupDataInput data, int appVersionCode, ParcelFileDescriptor newState)
    {
    }
}
