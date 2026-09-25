namespace FastNativeOps;

[Flags]
public enum StatFields
{
    None = 0,
    Size = 1,
    ModifiedTime = 2,
    /// <summary>Creation time as .NET reports it on Unix; see <see cref="DirectoryBatch.GetCreationTimeUtc"/>.</summary>
    CreationTime = 4,
    LastAccessTime = 8,
    /// <summary><see cref="FileAttributes"/> as .NET derives them on Unix; see <see cref="DirectoryBatch.GetAttributes"/>.</summary>
    Attributes = 16,
}
