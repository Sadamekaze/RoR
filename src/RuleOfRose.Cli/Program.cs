using RuleOfRose.Core;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine("""
            RoRText - Rule of Rose .ARA text translator (RTPK / ELFR101 module).

            Usage:
              RoRText export <ara> <json> [--merge <old.json>]
              RoRText import <ara> <json> <output>
              RoRText scan <ara>
              RoRText verify <ara> <json>

            export : scan the module and write a translation JSON. Entry ids come
                     from the referencing instruction, so they survive rebuilds.
            import : rebuild the archive with translated text. Entries are matched
                     by id, so the same JSON keeps working after the archive is
                     rebuilt; v2 documents (id = text offset) fall back to export
                     order when the archive was already modified.
            scan   : print structure, entries and remaining text budget.
            verify : re-scan and check the JSON against the binary.
            scan-dir <root>     : scan every .ARA under a folder (batch).
            export-dir <root> <outdir> : export one JSON per .ARA with
                     entries, mirroring the folder tree (batch extraction
                     of all game text).

            Translations may be LONGER than the source text (e.g. "Next" ->
            "Selanjutnya"). Keep the #R/#F prefix; use #n for line breaks.
            """);
        return 0;
    }

    string command = args[0];
    var positional = new List<string>();
    string? merge = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--merge" && i + 1 < args.Length) merge = args[++i];
        else positional.Add(args[i]);
    }

    switch (command)
    {
        case "export" when positional.Count == 2:
        {
            var doc = TextPatcher.Export(positional[0], merge);
            TranslationJson.Write(positional[1], doc);
            Console.WriteLine($"Exported {doc.Entries.Count} entries");
            break;
        }
        case "import" when positional.Count == 3:
        {
            string output = ConfirmOutput(positional[2]);
            var result = TextPatcher.Import(positional[0], positional[1], output);
            if (result.NoChanges) Console.WriteLine("No changes: identical output");
            else
                Console.WriteLine(
                    $"Changed {result.Changed} strings; added {result.AddedBytes} bytes; output {result.OutputBytes} bytes.");
            break;
        }
        case "scan" when positional.Count == 1:
            Scan(positional[0]);
            break;
        case "verify" when positional.Count == 2:
            Verify(positional[0], positional[1]);
            break;
        case "scan-dir" when positional.Count == 1:
            ScanDir(positional[0]);
            break;
        case "export-dir" when positional.Count == 2:
            ExportDir(positional[0], positional[1]);
            break;
        default:
            Console.Error.WriteLine("Error: Invalid command or arguments. Run --help.");
            return 1;
    }
    return 0;
}
catch (Exception e) when (e is InvalidDataException or IOException
    or UnauthorizedAccessException or System.Text.Json.JsonException
    or OverflowException or ArgumentException or System.Text.EncoderFallbackException)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}

// Core never prompts; the CLI asks about existing output files.
static string ConfirmOutput(string path)
{
    if (!File.Exists(path)) return path;
    Console.Write($"Overwrite {path}? [y/N] ");
    string? answer = Console.ReadLine();
    if (answer is "y" or "Y" or "yes" or "YES") return path;
    throw new InvalidDataException("Output file exists; aborting (no overwrite).");
}

static void Scan(string araPath)
{
    var ara = RtpkArchive.Load(araPath);
    var scan = ModuleScanner.Scan(ara.Module);
    Console.WriteLine($"File: {araPath}");
    Console.WriteLine($"Parts: {ara.Parts.Count}");
    Console.WriteLine($"Module size: {ara.Module.Length} bytes; text budget left: {0x30000 - ara.Module.Length} bytes");
    if (scan.AtViolations > 0)
        Console.WriteLine($"AT writes outside LUI at,0x45: {scan.AtViolations} (may be string bytes; import verifies every reference)");
    Console.WriteLine($"Entries: {scan.Entries.Count}");
    // Urut menurut id (offset instruksi), bukan offset teks, agar baris tidak
    // berpindah setelah arsip di-rebuild.
    foreach (var e in scan.Entries.OrderBy(e => e.Id, StringComparer.Ordinal))
        Console.WriteLine($"  {e.Id}  0x{e.ModuleOffset:X6}  {e.Source}");
}

static void Verify(string araPath, string jsonPath)
{
    var ara = RtpkArchive.Load(araPath);
    var scan = ModuleScanner.Scan(ara.Module);
    var doc = TranslationJson.Read(jsonPath);
    // v3: id stabil antar-rebuild, jadi JSON dari arsip versi sebelumnya sah
    // diperiksa di sini; entri dianggap cocok bila teks arsip sama dengan
    // source (belum dipatch) atau dengan translation (sudah dipatch).
    bool stableIds = doc.Format == TranslationDocument.CurrentFormat;
    if (!stableIds && doc.SourceSha256.Length > 0 && doc.SourceSha256 != ara.Sha256)
        throw new InvalidDataException("JSON source_sha256 does not match this ARA.");
    var byId = scan.Entries.ToDictionary(e => e.Id);
    // v2 dengan id yang tidak dikenal (id = offset teks): arsipnya sama (hash
    // cocok), sehingga urutannya identik — periksa per urutan seperti import.
    bool legacyByIndex = !stableIds && doc.Entries.Count == scan.Entries.Count
        && doc.Entries.Any(e => !byId.ContainsKey(e.Id));
    int longer = 0, shorter = 0, same = 0, missing = 0;
    for (int i = 0; i < doc.Entries.Count; i++)
    {
        var e = doc.Entries[i];
        TextEntryRecord s;
        if (legacyByIndex) s = scan.Entries[i];
        else if (byId.TryGetValue(e.Id, out var found)) s = found;
        else throw new InvalidDataException($"Unknown entry id: {e.Id}");
        if (e.Source != s.Source && (!stableIds || e.Translation != s.Source))
            throw new InvalidDataException(
                $"Entry {e.Id} in the archive matches neither its source nor its translation.");
        if (e.Translation.Length < 2 || e.Translation[0] != '#'
            || (e.Translation[1] != 'R' && e.Translation[1] != 'F'))
            throw new InvalidDataException(
                $"Translation {e.Id} harus diawali #R atau #F");
        if (e.Translation.Any(c => c < 32))
            throw new InvalidDataException(
                $"Karakter kontrol (<0x20) pada translation {e.Id}; gunakan #n untuk newline");
        int cmp = e.Translation.Length.CompareTo(s.Source.Length);
        if (cmp > 0) longer++; else if (cmp < 0) shorter++; else same++;
    }
    missing = scan.Entries.Count - doc.Entries.Count;
    Console.WriteLine(
        $"Entries in archive: {scan.Entries.Count}; in JSON: {doc.Entries.Count};" +
        $" translations: {longer} longer, {shorter} shorter, {same} same length," +
        $" {missing} kept original (not in JSON)");
    Console.WriteLine("OK");
}

static IEnumerable<string> EnumerateAra(string root)
    => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".ARA", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

static void ScanDir(string root)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine("Error: folder tidak ditemukan: " + root);
        Environment.ExitCode = 1;
        return;
    }
    int n = 0, withEntries = 0, totalEntries = 0, errors = 0, atBad = 0, noAt = 0;
    foreach (string f in EnumerateAra(root))
    {
        n++;
        string rel = Path.GetRelativePath(root, f);
        try
        {
            var ara = RtpkArchive.Load(f);
            var scan = ModuleScanner.Scan(ara.Module);
            if (scan.AtViolations > 0) atBad++;
            else if (scan.AtWrites == 0) noAt++;
            if (scan.Entries.Count > 0) { withEntries++; totalEntries += scan.Entries.Count; }
            string cls = scan.AtViolations > 0 ? "AT-VIOLATION" : scan.AtWrites == 0 ? "no-at" : "ok";
            Console.WriteLine($"{rel}\t{cls}\t{scan.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            errors++;
            Console.WriteLine($"{rel}\tERROR\t{ex.Message}");
        }
    }
    Console.WriteLine($"--- {n} .ARA: {withEntries} with entries, {totalEntries} entries total," +
        $" {noAt} no-at, {atBad} at-violation, {errors} errors");
}

static void ExportDir(string root, string outDir)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine("Error: folder tidak ditemukan: " + root);
        Environment.ExitCode = 1;
        return;
    }
    int exported = 0, empty = 0, errors = 0, total = 0;
    foreach (string f in EnumerateAra(root))
    {
        string rel = Path.GetRelativePath(root, f);
        try
        {
            var doc = TextPatcher.Export(f);
            if (doc.Entries.Count == 0) { empty++; continue; }
            string jsonPath = Path.Combine(outDir, Path.ChangeExtension(rel, ".text.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            TranslationJson.Write(jsonPath, doc);
            exported++;
            total += doc.Entries.Count;
            Console.WriteLine($"{rel} -> {jsonPath} ({doc.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            errors++;
            Console.WriteLine($"{rel}\tERROR\t{ex.Message}");
        }
    }
    Console.WriteLine($"--- exported {exported} JSON ({total} entries);" +
        $" {empty} ARA tanpa entry; {errors} errors");
}
