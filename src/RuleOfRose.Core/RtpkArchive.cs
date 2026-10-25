using System.Security.Cryptography;
using System.Text;

namespace RuleOfRose.Core;

// RTPK container: magic@0, total length u32@4, part count u16@14,
// sizes u32@32, offsets u32@32+4n. Every header byte outside
// length/count/sizes/offsets is preserved verbatim by Rebuild.
public static class RtpkArchive
{
    public static uint U(byte[] b, int p) => BitConverter.ToUInt32(b, p);
    public static int I32(byte[] b, int p) => BitConverter.ToInt32(b, p);
    public static ushort U16(byte[] b, int p) => BitConverter.ToUInt16(b, p);

    public static void Put(byte[] b, int p, uint v) => BitConverter.TryWriteBytes(b.AsSpan(p), v);

    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static int Align(int v, int a = 16) => (v + a - 1) / a * a;

    public static AraArchive Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, 4) != "RTPK")
            throw new InvalidDataException("File bukan container RTPK yang valid: " + path);
        uint total = U(bytes, 4);
        if (total != bytes.Length)
            throw new InvalidDataException(
                $"Panjang total RTPK ({total}) tidak sama dengan ukuran file ({bytes.Length}).");
        int n = U16(bytes, 14);
        if (n < 1 || 32L + 8L * n > bytes.Length)
            throw new InvalidDataException("Jumlah bagian RTPK tidak wajar: " + n);
        var sizes = new int[n];
        var offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            sizes[i] = checked((int)U(bytes, 32 + 4 * i));
            offsets[i] = checked((int)U(bytes, 32 + 4 * n + 4 * i));
            if (offsets[i] < 0 || sizes[i] < 0 || (long)offsets[i] + sizes[i] > bytes.Length)
                throw new InvalidDataException($"Bagian RTPK ke-{i} di luar batas file.");
        }
        var parts = new List<byte[]>(n);
        for (int i = 0; i < n; i++)
        {
            var part = new byte[sizes[i]];
            Array.Copy(bytes, offsets[i], part, 0, sizes[i]);
            parts.Add(part);
        }
        int module = -1;
        for (int i = 0; i < n; i++)
        {
            if (parts[i].Length >= 8 && Encoding.ASCII.GetString(parts[i], 0, 8) == "ELFR101\0")
            { module = i; break; }
        }
        if (module < 0)
            throw new InvalidDataException("Container RTPK tidak memiliki bagian modul ELFR101.");
        return new AraArchive(bytes, parts, module, Sha256Hex(bytes));
    }

    // Re-derive sizes/offsets/total from parts. All other header bytes are
    // copied from the original archive.
    public static byte[] Rebuild(byte[] original, IReadOnlyList<byte[]> parts)
    {
        int n = parts.Count;
        int headerLen = 32 + 8 * n;
        if (original.Length < headerLen)
            throw new InvalidDataException("Header RTPK asli lebih pendek dari jumlah bagian.");
        int capacity = Align(headerLen);
        for (int i = 0; i < n; i++) capacity += parts[i].Length + 15;
        var buffer = new byte[capacity];
        Array.Copy(original, buffer, headerLen);
        int pos = Align(headerLen);
        for (int i = 0; i < n; i++)
        {
            WriteU32(buffer, 32 + 4 * i, (uint)parts[i].Length);
            WriteU32(buffer, 32 + 4 * n + 4 * i, (uint)pos);
            Array.Copy(parts[i], 0, buffer, pos, parts[i].Length);
            pos = Align(pos + parts[i].Length);
        }
        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        WriteU32(result, 4, (uint)pos);
        return result;
    }

    private static void WriteU32(byte[] b, int p, uint v) => BitConverter.TryWriteBytes(b.AsSpan(p), v);
}
