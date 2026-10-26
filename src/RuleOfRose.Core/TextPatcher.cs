using System.Text;

namespace RuleOfRose.Core;

// Import rebuilds from whatever ARA is given. Entry ids point at the
// referencing instruction, so they survive a rebuild and a v3 JSON is applied
// by id regardless of which rebuild it came from. A v2 JSON (ids were text
// offsets) keeps the old behaviour: matched by id when the hash matches,
// otherwise by export order.
//
// Re-patch: translated strings go to two pools. Text addressed by `$gp` is
// appended after the module data (gp keeps its value), so the reference stays a
// plain `ADDIU rt,gp,imm`; LUI/ADDIU text whose base follows gp moves with it.
// Mengganti rs ke $AT membuat teks tidak terbaca di game, jadi bentuk
// gp_addiu tidak pernah diubah.
public static class TextPatcher
{
    public static TranslationDocument Export(string araPath, string? mergePath = null)
    {
        var ara = RtpkArchive.Load(araPath);
        var scan = ModuleScanner.Scan(ara.Module);
        var doc = new TranslationDocument
        {
            SourceSha256 = ara.Sha256,
            Scope = $"{scan.Entries.Count} referenced text entries: LUI/ADDIU (semua hi) + GP/AT-relative; not all game text",
            Entries = scan.Entries,
        };
        if (mergePath is not null)
        {
            var old = TranslationJson.Read(mergePath);
            if (old.SourceSha256 != ara.Sha256)
                throw new InvalidDataException("Merge source hash mismatch");
            if (old.Format != TranslationDocument.CurrentFormat)
            {
                // v2: id = offset teks, tidak sepadan dengan skema v3. Arsipnya
                // sama (hash cocok), jadi urutan entri identik — pasangkan
                // per urutan, bukan per id.
                if (old.Entries.Count != scan.Entries.Count)
                    throw new InvalidDataException("Merge source mismatch");
                for (int i = 0; i < scan.Entries.Count; i++)
                {
                    if (old.Entries[i].Source != scan.Entries[i].Source)
                        throw new InvalidDataException("Merge source mismatch");
                    scan.Entries[i].Translation = old.Entries[i].Translation;
                }
            }
            else
            {
                var byId = new Dictionary<string, TextEntryRecord>();
                foreach (var e in old.Entries)
                {
                    if (!byId.TryAdd(e.Id, e))
                        throw new InvalidDataException("Unknown or duplicate merge IDs");
                }
                var ids = scan.Entries.Select(e => e.Id).ToHashSet();
                if (!byId.Keys.All(ids.Contains))
                    throw new InvalidDataException("Unknown or duplicate merge IDs");
                foreach (var e in scan.Entries)
                {
                    if (!byId.TryGetValue(e.Id, out var prev)) continue;
                    if (prev.Source != e.Source)
                        throw new InvalidDataException("Merge source mismatch");
                    e.Translation = prev.Translation;
                }
            }
        }
        // Urutan JSON mengikuti id (offset instruksi), bukan offset teks, supaya
        // sama di tiap rebuild dan diff-nya tidak berisik. Pencocokan saat
        // import berjalan lewat id, jadi urutan tidak memengaruhi hasil.
        doc.Entries = scan.Entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        return doc;
    }

    // Basis (ruang offset modul) tiap gaya referensi: LUI hi=0x41/0x45 ->
    // specialLuiBase; 0x42/0x46 -> specialLuiBase+0x10000; hi lain ->
    // native hi<<16 (VA = offset + (0x410000-basis)). gp_addiu/at_addiu juga
    // basis (AT = basis dimulai dari specialLuiBase).
    private static long RefBase(TextRef r, long basis) => r.Kind switch
    {
        "gp_addiu" or "at_addiu" => basis,
        "lui_addiu" => r.HiWord switch
        {
            0x41 or 0x45 or null => basis,
            0x42 or 0x46 => basis + 0x10000,
            { } h => basis + ((long)(h - 0x41) << 16),
        },
        _ => basis,
    };

    public static ImportResult Import(string araPath, string jsonPath, string outputPath)
    {
        var ara = RtpkArchive.Load(araPath);
        byte[] m = ara.Module;
        var scan = ModuleScanner.Scan(m);
        var doc = TranslationJson.Read(jsonPath);
        var entries = scan.Entries;

        // v3: id berasal dari offset instruksi referensi, jadi stabil antar-
        // rebuild — entri dicocokkan lewat id dan dokumen lama tetap berlaku.
        // v2 (dokumen lama): id = offset teks yang bergeser setelah patch, jadi
        // pertahankan cara lama: per id bila hash sama, per urutan export bila
        // arsipnya sudah dimodifikasi.
        bool stableIds = doc.Format == TranslationDocument.CurrentFormat;
        bool rePatch = !stableIds && doc.SourceSha256.Length > 0 && doc.SourceSha256 != ara.Sha256;

        var docById = new Dictionary<string, TextEntryRecord>();
        foreach (var e in doc.Entries)
        {
            if (!docById.TryAdd(e.Id, e))
                throw new InvalidDataException($"ID entri ganda di JSON: {e.Id}");
        }
        var scanIds = entries.Select(e => e.Id).ToHashSet();
        if (stableIds)
        {
            var unknown = doc.Entries.FirstOrDefault(e => !scanIds.Contains(e.Id));
            if (unknown is not null)
                throw new InvalidDataException(
                    $"Entri {unknown.Id} di JSON tidak ada di arsip ini; JSON mungkin berasal dari arsip lain.");
        }
        else if (doc.Entries.Count != entries.Count)
        {
            throw new InvalidDataException(rePatch
                ? $"Jumlah entri JSON ({doc.Entries.Count}) tidak sama dengan entri terdeteksi dalam ARA ({entries.Count}); re-patch membutuhkan jumlah entri identik."
                : $"Jumlah entri JSON ({doc.Entries.Count}) tidak sama dengan entri terdeteksi dalam ARA ({entries.Count}).");
        }

        // v2 dengan id yang tidak dikenal (id = offset teks dari sebelum skema
        // v3): arsipnya sama bila hash cocok, sehingga urutannya identik —
        // pasangkan per urutan dan tetap validasi source.
        bool legacyByIndex = !stableIds && !rePatch
            && doc.Entries.Any(e => !scanIds.Contains(e.Id));

        var edits = new Dictionary<string, TextEntryRecord>();
        if (!stableIds && !rePatch && !legacyByIndex)
        {
            foreach (var e in doc.Entries) edits[e.Id] = e;
            foreach (var e in scan.Entries) edits.TryAdd(e.Id, e);
        }

        // Entri tanpa pasangan di JSON (v3) tetap memakai teks asli.
        TextEntryRecord EditFor(int i)
        {
            var e = entries[i];
            if (stableIds)
                return docById.TryGetValue(e.Id, out var d) ? d : e;
            if (rePatch) return doc.Entries[i];
            var forEntry = legacyByIndex ? doc.Entries[i] : edits[e.Id];
            if (forEntry.Source != e.Source)
                throw new InvalidDataException("Source field changed");
            return forEntry;
        }

        // Teks yang berubah dibagi dua blok menurut cara rujukannya:
        //  - shiftPool: rujukan LUI/ADDIU dengan basis specialLuiBase (hi 0x41 /
        //    0x45). Jendela 16-bit-nya tidak sampai ujung modul, jadi blok ini
        //    disisipkan sebelum teks pertama dan seluruh isi modul bergeser;
        //    basis ikut bergeser bersama gp, sehingga rujukan lama tetap
        //    menunjuk teks yang benar.
        //  - appendPool: rujukan `ADDIU rt,gp,imm` (dan LUI dengan hi lain,
        //    mis. 0x42 yang berbasis specialLuiBase+0x10000). Blok ini
        //    ditambahkan SESUDAH modul, jadi tidak ada byte lama yang bergeser
        //    dan bentuk gp_addiu tidak perlu diubah ke AT — game tidak membaca
        //    teks yang dirujuk lewat AT.
        var shiftPool = new List<byte>();
        var appendPool = new List<byte>();
        var changes = new List<(TextEntryRecord Entry, int Offset, bool AppendBlock)>();
        for (int i = 0; i < entries.Count; i++)
        {
            TextEntryRecord e = entries[i];
            TextEntryRecord edit = EditFor(i);
            string t = edit.Translation;
            bool appendBlock = e.References.Any(r =>
                !(r.Kind == "lui_addiu" && (r.HiWord is null or 0x41 or 0x45)));
            // Entri dengan referensi at_addiu berasal dari patch versi lama yang
            // rs-nya sudah diubah ke AT: teksnya ikut dipindah supaya rujukannya
            // bisa dikembalikan ke $gp walau terjemahannya sama.
            bool legacyAt = e.References.Any(r => r.Kind == "at_addiu");
            if (t == e.Source && !legacyAt) continue;
            if (t.Length < 2 || t[0] != '#' || (t[1] != 'R' && t[1] != 'F'))
                throw new InvalidDataException(
                    $"Translation {e.Id} harus diawali #R atau #F — tanpa prefix itu teks" +
                    " tidak akan terbaca ulang scanner maupun ditampilkan game");
            if (t.Any(c => c < 32))
                throw new InvalidDataException(
                    "Karakter kontrol (<0x20) dilarang; gunakan #n untuk newline" +
                    " (byte >=0x80/SJIS diizinkan)");
            List<byte> pool = appendBlock ? appendPool : shiftPool;
            changes.Add((e, pool.Count, appendBlock));
            pool.AddRange(Encoding.Latin1.GetBytes(t));
            pool.Add(0);
            while (pool.Count % 4 != 0) pool.Add(0);
        }
        if (changes.Count == 0)
        {
            File.WriteAllBytes(outputPath, ara.Bytes);
            return new ImportResult(0, 0, ara.Bytes.Length, true);
        }

        while (shiftPool.Count % 16 != 0) shiftPool.Add(0);
        while (appendPool.Count % 16 != 0) appendPool.Add(0);
        int deltaShift = shiftPool.Count;
        int dataStart = checked((int)RtpkArchive.U(m, 20));
        // Sisipan di teks pertama (atau dataStart) agar semua string lama ikut
        // bergeser bersama gp; tanpa blok sisipan, modul tidak diubah sama sekali.
        int insert = m.Length;
        if (deltaShift > 0)
        {
            insert = dataStart;
            if (entries.Count > 0 && entries[0].ModuleOffset < insert) insert = entries[0].ModuleOffset;
        }
        // Blok tambahan diletakkan sesudah alamat terjauh yang dirujuk pola
        // referensi mana pun (teks atau bukan), supaya rujukan lama tidak
        // tiba-tiba mengenai string baru dan tidak ada data yang tertimpa.
        int appendBase = m.Length + deltaShift;
        if (appendPool.Count > 0)
        {
            int gapBase = RtpkArchive.Align(Math.Max(m.Length, (int)scan.MaxRefTarget + 1));
            appendBase = gapBase + deltaShift;
        }
        var nm = new byte[appendBase + appendPool.Count];
        Array.Copy(m, 0, nm, 0, insert);
        shiftPool.CopyTo(nm, insert);
        Array.Copy(m, insert, nm, insert + deltaShift, m.Length - insert);
        if (appendPool.Count > 0) appendPool.CopyTo(nm, appendBase);
        if (!nm.AsSpan(insert + deltaShift, m.Length - insert).SequenceEqual(m.AsSpan(insert)))
            throw new InvalidDataException("Data asli setelah pool tidak identik");
        if (nm.Length > 0x30000)
            throw new InvalidDataException("Module exceeds fixed copy/work area");
        if (deltaShift > 0)
        {
            // Isi modul bergeser, jadi gp dan specialLuiBase ikut bergeser.
            RtpkArchive.Put(nm, 20, (uint)(dataStart + deltaShift));
        }
        int tailGrowth = nm.Length - m.Length - deltaShift;
        if (tailGrowth > 0)
        {
            // Blok tambahan ada di ujung modul (sesudah alamat terjauh yang
            // dirujuk instruksi) dan posisinya relatif terhadap gp harus tetap
            // (tidak ikut bergeser). Header tetap konsisten
            // (dataStart + dataSize = panjang modul, seperti modul asli):
            //   gp = dataStart + dataSize + bias + 0x8000
            // dataSize tumbuh sebesar blok itu dan bias (i32@12) turun sebesar itu.
            uint dataSize = RtpkArchive.U(nm, 24);
            int bias = RtpkArchive.I32(nm, 12);
            RtpkArchive.Put(nm, 24, dataSize + (uint)tailGrowth);
            RtpkArchive.Put(nm, 12, unchecked((uint)(bias - tailGrowth)));
        }
        var (gp, basis) = ModuleScanner.Bases(nm);
        var (gpBefore, basisBefore) = ModuleScanner.Bases(m);
        if (gp - gpBefore != deltaShift || basis - basisBefore != deltaShift)
            throw new InvalidDataException("GP/basis bergeser tidak sesuai tata letak pool");
        foreach (var (e, offset, appendBlock) in changes)
        {
            int target = (appendBlock ? appendBase : insert) + offset;
            foreach (var r in e.References)
            {
                // gp_addiu tetap gp_addiu (rs=$gp); at_addiu hanya muncul di
                // arsip hasil patch versi lama, dan ikut dikembalikan ke gp.
                bool gpRelative = r.Kind is "gp_addiu" or "at_addiu";
                long rbase = gpRelative ? gp : RefBase(r, basis);
                long imm = target - rbase;
                if (imm < -32768 || imm > 32767)
                    throw new InvalidDataException(
                        $"Teks baru {e.Id} di luar jendela immediate untuk referensi {r.Kind}" +
                        (r.HiWord is { } h ? $" (LUI hi=0x{h:X})" : "") +
                        "; kurangi panjang teks");
                int loc = r.InstructionOffset;
                uint word = RtpkArchive.U(nm, loc);
                if (gpRelative)
                    word = (word & ~(31u << 21)) | (28u << 21); // rs = $gp (jangan AT)
                RtpkArchive.Put(nm, loc, (word & 0xFFFF0000) | ((uint)imm & 0xFFFF));
            }
        }

        // Resolve every exported reference through the actual mapping.
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            TextEntryRecord edit = EditFor(i);
            byte[] expected = Encoding.Latin1.GetBytes(edit.Translation).Append((byte)0).ToArray();
            foreach (var r in e.References)
            {
                uint word = RtpkArchive.U(nm, r.InstructionOffset);
                long regbase = ((word >> 21) & 31) == 28 ? gp : RefBase(r, basis);
                long q = regbase + ModuleScanner.Signed((int)(word & 0xFFFF));
                if (q < 0 || q + expected.Length > nm.Length
                    || !nm.AsSpan((int)q, expected.Length).SequenceEqual(expected))
                    throw new InvalidDataException($"Verifikasi referensi gagal untuk {e.Id} @0x{r.InstructionOffset:X}");
            }
        }

        var parts = ara.Parts.ToList();
        parts[ara.ModulePartIndex] = nm;
        byte[] result = RtpkArchive.Rebuild(ara.Bytes, parts);
        for (int i = 0; i < parts.Count; i++)
        {
            int o = checked((int)RtpkArchive.U(result, 32 + 4 * parts.Count + 4 * i));
            int s = checked((int)RtpkArchive.U(result, 32 + 4 * i));
            if (!result.AsSpan(o, s).SequenceEqual(parts[i]))
                throw new InvalidDataException($"Bagian RTPK ke-{i} tidak identik setelah rebuild");
        }
        File.WriteAllBytes(outputPath, result);
        return new ImportResult(changes.Count, nm.Length - m.Length, result.Length, false);
    }
}
