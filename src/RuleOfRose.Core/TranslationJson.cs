using System.Text.Encodings.Web;
using System.Text.Json;

namespace RuleOfRose.Core;

// JSON layout: 2-space indent, raw UTF-8 (no BOM), no trailing newline,
// same property order as the model classes.
public static class TranslationJson
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string path, TranslationDocument doc)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Opts), new System.Text.UTF8Encoding(false));
    }

    public static TranslationDocument Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TranslationDocument>(File.ReadAllText(path), Opts)
                ?? throw new InvalidDataException("JSON dokumen kosong.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON tidak valid: " + ex.Message);
        }
    }
}
