namespace FastNativeOps;

/// <summary>How <see cref="FastDirectory.EnumerateBatchBuffers"/> treats symbolic links.</summary>
public enum SymlinkHandling
{
    /// <summary>The link itself: <see cref="EntryType.SymbolicLink"/>, and its own attributes (<see cref="FileAttributes.ReparsePoint"/> only). No extra stat.</summary>
    Report,
    /// <summary>
    /// What <c>System.IO</c> does: the link reports the type of its target (Directory, File, Other), and
    /// <see cref="StatFields.Attributes"/> adds Directory / ReadOnly from the target. A link that cannot be resolved
    /// stays <see cref="EntryType.SymbolicLink"/>. One extra statx per link.
    /// </summary>
    Follow,
}
