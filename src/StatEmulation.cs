namespace FastNativeOps;

/// <summary>Portable (slower) stat via System.IO; used off Linux and as a fallback when statx is unavailable.</summary>
internal static class StatEmulation
{
    public static void Fill(string dir, DirectoryBatch batch, int parallelism = 1)
    {
        if (parallelism > 1 && batch.Count > 1)
            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                i => FillOne(dir, batch, i));
        else
            for (int i = 0; i < batch.Count; i++) FillOne(dir, batch, i);
    }

    public static void FillOne(string dir, DirectoryBatch batch, int i)
    {
        var s = EntryStat.Missing;
        try
        {
            var path = Path.Join(dir, batch.GetName(i));
            FileSystemInfo fi = batch.GetType(i) == EntryType.Directory ? new DirectoryInfo(path) : new FileInfo(path);
            if (fi.Exists || fi.LinkTarget is not null)
            {
                var fields = batch.Fields;
                if ((fields & StatFields.Size) != 0) s.Size = fi is FileInfo f && f.Exists ? f.Length : 0;
                if ((fields & StatFields.ModifiedTime) != 0) s.ModifiedTicks = fi.LastWriteTimeUtc.Ticks;
                if ((fields & StatFields.CreationTime) != 0) s.CreationTicks = fi.CreationTimeUtc.Ticks;
                if ((fields & StatFields.LastAccessTime) != 0) s.AccessTicks = fi.LastAccessTimeUtc.Ticks;
                if ((fields & StatFields.Attributes) != 0) s.Attributes = fi.Attributes;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        batch.SetStat(i, s);
    }
}
