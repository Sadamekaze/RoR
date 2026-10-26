using System.Text.Json.Serialization;

namespace RuleOfRose.Core;

// One reference instruction pointing at a text string in the module.
// Kind: "lui_addiu" (LUI rt,hi + ADDIU; base mengikuti hi — 0x41/0x45 ->
//       specialLuiBase, 0x42/0x46 -> specialLuiBase+0x10000,
//       hi lain -> native hi<<16),
//       "gp_addiu" (ADDIU rt,gp,imm — saat patch rs tetap $gp; game tidak
//       membaca teks yang dirujuk lewat AT hasil rewrite), atau
//       "at_addiu" (ADDIU rt,at,imm dengan AT = specialLuiBase; hanya muncul
//       di arsip hasil patch versi lama — import mengembalikannya ke $gp).
public sealed class TextRef
{
    public TextRef() { }
    public TextRef(string kind, int instructionOffset, ushort? hiWord = null)
    { Kind = kind; InstructionOffset = instructionOffset; HiWord = hiWord; }

    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("instruction_offset")] public int InstructionOffset { get; set; }
    // LUI hi untuk ref lui_addiu di luar keluarga 0x41/0x45; null = basis
    // (0x41/0x45) atau non-LUI — tidak ditulis ke JSON bila null.
    [JsonPropertyName("hi_word")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ushort? HiWord { get; set; }
}

public sealed class TextEntryRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    // May be LONGER than Source: changed strings move to a pool (after the
    // module data when the reference uses $gp, before the first text when it
    // uses the LUI/ADDIU base) and every reference is re-patched.
    [JsonPropertyName("translation")] public string Translation { get; set; } = "";
    // Hasil scan, bukan data terjemahan: dipakai patcher/CLI/GUI di memori dan
    // tidak ditulis ke JSON — nilainya selalu bisa dihitung ulang dari .ARA.
    [JsonIgnore] public int ModuleOffset { get; set; }
    [JsonIgnore] public List<TextRef> References { get; set; } = [];
}

public sealed class TranslationDocument
{
    // v3: id diturunkan dari offset instruksi referensi (stabil antar-rebuild)
    // dan import mencocokkan entri lewat id. v2 memakai offset teks sebagai id.
    public const string CurrentFormat = "ror-a164-v3";
    [JsonPropertyName("format")] public string Format { get; set; } = CurrentFormat;
    [JsonPropertyName("source_sha256")] public string SourceSha256 { get; set; } = "";
    [JsonPropertyName("entries")] public List<TextEntryRecord> Entries { get; set; } = [];
}

// Result of TextPatcher.Import.
public sealed record ImportResult(int Changed, int AddedBytes, int OutputBytes, bool NoChanges);

// Structural scan of one ELFR module: emulated bases and detected text
// entries. AtViolations menghitung word yang menulis AT di luar LUI at,0x45;
// nilainya informasi saja (byte string di region kode ikut terhitung), bukan
// alasan menolak patch — Import memverifikasi setiap referensi.
// MaxRefTarget = alamat terjauh yang dirujuk pola referensi mana pun (teks
// atau bukan), dipakai agar blok teks tambahan diletakkan sesudahnya.
public sealed record ModuleScan(long Gp, long BaseAddress, int AtWrites, int AtViolations,
    long MaxRefTarget, List<TextEntryRecord> Entries);

// Parsed RTPK container. ModulePartIndex locates the ELFR101 part (not hard-coded).
public sealed record AraArchive(byte[] Bytes, List<byte[]> Parts, int ModulePartIndex, string Sha256)
{
    public byte[] Module => Parts[ModulePartIndex];
}
