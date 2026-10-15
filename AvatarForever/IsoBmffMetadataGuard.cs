using System;
using System.Buffers.Binary;

namespace FurkanGozukara.SwarmExtensions.AvatarForever;

/// <summary>Bounded top-level container check before handing MP4/MOV to ATL.</summary>
internal static class IsoBmffMetadataGuard
{
    internal static bool HasCompleteMovie(ReadOnlySpan<byte> data)
    {
        bool hasMovie = false;
        int offset = 0;
        while (offset < data.Length)
        {
            ReadOnlySpan<byte> box = data[offset..];
            if (box.Length < 8) return false;
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(box);
            int headerSize = 8;
            if (size == 1)
            {
                if (box.Length < 16) return false;
                size = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = (ulong)box.Length; // Legal box extending to EOF.
            }
            if (size < (ulong)headerSize || size > (ulong)box.Length) return false;
            if (box.Slice(4, 4).SequenceEqual("moov"u8)) hasMovie = true;
            offset += (int)size; // Bounded by the remaining span; always progresses.
        }
        return hasMovie;
    }
}
