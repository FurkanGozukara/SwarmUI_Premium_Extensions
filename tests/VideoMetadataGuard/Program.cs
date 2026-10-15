using System.Buffers.Binary;
using FurkanGozukara.SwarmExtensions.AvatarForever;

static byte[] Box(string type, uint size = 8)
{
    byte[] data = new byte[Math.Max(size, 8)];
    BinaryPrimitives.WriteUInt32BigEndian(data, size);
    System.Text.Encoding.ASCII.GetBytes(type).CopyTo(data, 4);
    return data;
}
int checks = 0;
void Check(string name, bool expected, byte[] data)
{
    if (IsoBmffMetadataGuard.HasCompleteMovie(data) != expected)
        throw new Exception(name);
    checks++;
}
Check("empty", false, []);
Check("interrupted encode, mdat to EOF without moov", false, [..Box("ftyp"), ..Box("mdat", 0)]);
Check("ordinary finalized movie", true, [..Box("ftyp"), ..Box("mdat"), ..Box("moov")]);
Check("faststart with legal mdat to EOF", true, [..Box("moov"), ..Box("mdat", 0)]);
Check("truncated header", false, [..Box("moov"), 0, 0, 0]);
Check("undersized box", false, Box("moov", 4));
byte[] oversized = Box("moov");
BinaryPrimitives.WriteUInt32BigEndian(oversized, uint.MaxValue);
Check("oversized box", false, oversized);
byte[] extended = new byte[16];
BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
"moov"u8.CopyTo(extended.AsSpan(4));
BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), 16);
Check("64-bit size", true, extended);
BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), ulong.MaxValue);
Check("64-bit overflow", false, extended);
Check("truncated 64-bit size", false, [0, 0, 0, 1, 109, 111, 111, 118]);
if (args.Length > 0) Check("actual interrupted diagnostic", false, File.ReadAllBytes(args[0]));
Console.WriteLine($"PASS: {checks} container checks");
