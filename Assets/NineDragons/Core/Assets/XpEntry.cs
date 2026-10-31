using System;

namespace NineDragons.Assets
{
    /// <summary>
    /// One packed file inside a <c>.XP</c> archive (an entry in the archive's directory).
    /// Ported from the client's <c>XFILE_INFO</c> struct (XPackage.h) — the C++ reader keeps
    /// {strFileName, nLength, nFilePosition} per entry. Sizes are derived from the gap to the
    /// next entry's position (entries are stored back-to-back in position order).
    /// </summary>
    public sealed class XpEntry
    {
        /// <summary>Zero-based index of the entry in the archive directory (== C++ file_id).</summary>
        public int Index { get; }

        /// <summary>
        /// Decoded file name (the on-disk name is XOR-0xEF encoded). Case is preserved as decoded;
        /// the C++ client uppercases ASCII a-z internally, but all lookups here are case-insensitive.
        /// </summary>
        public string Name { get; }

        /// <summary>Absolute byte offset of this entry's data block from the start of the .XP file.</summary>
        public long Offset { get; }

        /// <summary>Size of this entry's data block in bytes.</summary>
        public int Size { get; }

        public XpEntry(int index, string name, long offset, int size)
        {
            Index = index;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Offset = offset;
            Size = size;
        }

        public override string ToString() => $"[{Index}] {Name} (off={Offset}, size={Size})";
    }
}
