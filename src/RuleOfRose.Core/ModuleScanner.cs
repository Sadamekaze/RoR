using System.Text;

namespace RuleOfRose.Core;

// Static scan of the ELFR module.
// Text strings start with #R/#F and are NUL-terminated.
// References: lui_addiu (LUI rt,0x41 + ADDIU, base = specialLuiBase) and
// gp_addiu (ADDIU rt,gp,imm). Every AT write must be LUI at,0x45.
public static class ModuleScanner
{
    public const uint AtLuiInstruction = 0x3C010045; // LUI at,0x45

    public static int Signed(int v) => (v & 0x8000) != 0 ? v - 0x10000 : v;

    // Emulated GP and special LUI base used by the text renderer.
    public static (long Gp, long Base) Bases(byte[] m)
    {
        long gp = (uint)RtpkArchive.U(m, 20) + (uint)RtpkArchive.U(m, 24)
                + RtpkArchive.I32(m, 12) + 0x8000;
        long basis = gp + 0x450000L - RtpkArchive.U(m, 16);
        return (gp, basis);
    }

    public static ModuleScan Scan(byte[] m)
    {
        if (m.Length < 0x30)
            throw new InvalidDataException("Modul terlalu kecil untuk header ELFR yang valid.");
        var (gp, basis) = Bases(m);
        int dataStart = checked((int)RtpkArchive.U(m, 20));
        if (dataStart < 0x30 || dataStart > m.Length)
            throw new InvalidDataException("Field data modul tidak wajar.");

        // The loader seeds AT with specialLuiBase; LUI at,0x45 preserves it.
        // Modules with zero AT writes are safe too (AT stays seeded).
        // Other AT writers are only counted: byte string di region ini bisa
        // terbaca sebagai penulisan AT, jadi jumlahnya tidak dipakai untuk
        // menolak patch — Import memverifikasi tiap referensi setelah rebuild.
        int atWrites = 0, atViolations = 0;
        for (int p = 0x30; p + 4 <= dataStart; p += 4)
        {
            uint w = RtpkArchive.U(m, p);
            int op = (int)(w >> 26), rt = (int)((w >> 16) & 31), rd = (int)((w >> 11) & 31);
            bool immOp = (op >= 8 && op <= 15) || (op >= 32 && op <= 39);
            bool writesAt = (immOp && rt == 1)
                || (op == 0 && rd == 1 && (w & 63) != 8 && (w & 63) != 12 && (w & 63) != 13
                    && (w & 63) != 17 && (w & 63) != 19 && (w & 63) != 24 && (w & 63) != 25
                    && (w & 63) != 26 && (w & 63) != 27);
            if (!writesAt) continue;
            if (w == AtLuiInstruction) atWrites++;
            else atViolations++;
        }

        var found = new Dictionary<int, TextEntryRecord>();
        // Alamat terjauh yang dirujuk pola referensi mana pun (walau tidak
        // menunjuk teks). Dipakai TextPatcher agar blok teks tambahan tidak
        // menimpa alamat yang sudah dipakai instruksi lain dan tidak membuat
        // rujukan lama tiba-tiba mengenai string baru.
        long maxRef = 0;
        for (int p = 0x30; p + 4 <= dataStart; p += 4)
        {
            uint w = RtpkArchive.U(m, p);
            long q; TextRef r;
            if ((w >> 26) == 9 && ((w >> 21) & 31) == 28)
            {
                q = gp + Signed((int)(w & 0xFFFF));
                r = new TextRef("gp_addiu", p);
            }
            else if ((w >> 26) == 9 && ((w >> 21) & 31) == 1)
            {
                // AT is seeded with specialLuiBase by the loader; modules
                // without other AT writes keep it. Bentuk ini hanya tersisa di
                // arsip hasil patch versi lama (gp_addiu yang dulu di-rewrite
                // ke AT); import menulis ulang ke $gp karena game tidak
                // membaca teks lewat basis AT hasil patch.
                q = basis + Signed((int)(w & 0xFFFF));
                r = new TextRef("at_addiu", p);
            }
            else if (p + 8 <= dataStart)
            {
                uint hi = w, lo = RtpkArchive.U(m, p + 4);
                if ((hi >> 26) == 15 && (lo >> 26) == 9
                    && ((hi >> 16) & 31) == ((lo >> 21) & 31))
                {
                    // Reference bases: LUI hi=0x41/0x45 -> specialLuiBase;
                    // 0x42/0x46 -> specialLuiBase+0x10000;
                    // other hi -> native hi<<16 (VA = offset + (0x410000-basis)).
                    ushort h = (ushort)(hi & 0xFFFF);
                    long hbase = h switch
                    {
                        0x41 or 0x45 => basis,
                        0x42 or 0x46 => basis + 0x10000,
                        _ => basis + ((long)(h - 0x41) << 16),
                    };
                    q = hbase + Signed((int)(lo & 0xFFFF));
                    r = new TextRef("lui_addiu", p + 4,
                        h is 0x41 or 0x45 ? null : h);
                }
                else continue;
            }
            else continue;

            if (q >= 0 && q < 0x30000) maxRef = Math.Max(maxRef, q);
            if (q < 0 || q + 2 > m.Length) continue;
            if (m[(int)q] != '#' || (m[(int)q + 1] != 'R' && m[(int)q + 1] != 'F')) continue;
            int end = Array.IndexOf(m, (byte)0, (int)q);
            if (end < 0) continue;
            var raw = new byte[end - (int)q];
            Array.Copy(m, (int)q, raw, 0, raw.Length);
            // Semua byte >=0x20: ASCII cetak + byte >=0x80 (penanda game
            // \x81.. dan teks Shift-JIS pada modul versi penuh). Kontrol
            // <0x20 ditolak; NUL mengakhiri run.
            if (raw.Any(c => c < 32)) continue;
            if (!found.TryGetValue((int)q, out var e))
            {
                string text = Encoding.Latin1.GetString(raw);
                e = new TextEntryRecord
                {
                    Source = text,
                    Translation = text,
                    ModuleOffset = (int)q,
                };
                found[(int)q] = e;
            }
            e.References.Add(r);
        }
        var entries = found.Values.OrderBy(e => e.ModuleOffset).ToList();
        // ID dari offset instruksi referensi pertama, bukan offset teks. Teks
        // pindah ke pool tiap kali di-patch, instruksinya tidak — sehingga ID
        // tetap sama setelah rebuild dan JSON lama tetap bisa dipakai ulang.
        foreach (var e in entries) e.Id = $"text_{e.References.Min(r => r.InstructionOffset):X6}";
        return new ModuleScan(gp, basis, atWrites, atViolations, maxRef, entries);
    }
}
