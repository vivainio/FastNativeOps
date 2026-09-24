namespace FastNativeOps;

/// <summary>Portable (slower) stat via System.IO; used off Linux and as a fallback when statx is unavailable.</summary>
internal static class StatEmulation
{
    public static void Fill(string dir, DirectoryBatch batch, int start = 0)
    {
        for (int i = start; i < batch.Count; i++) FillOne(dir, batch, i);
    }

    public static void FillOne(string dir, DirectoryBatch batch, int i)
    {
        long size = -1, mtime = DateTime.MinValue.Ticks;
        try
        {
            var path = Path.Join(dir, batch.GetName(i));
            FileSystemInfo fi = batch.GetType(i) == EntryType.Directory ? new DirectoryInfo(path) : new FileInfo(path);
            if (fi.Exists || fi.LinkTarget is not null)
            {
                size = fi is FileInfo f && f.Exists ? f.Length : 0;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        batch.SetStat(i, size, mtime);
    }
}
