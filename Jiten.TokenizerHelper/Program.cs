// GSM_JITEN_OFFLINE_JMDICT_PIPELINE_V1
// GSM_JITEN_OFFLINE_JMDICT_COMPILEFIX_V1
// JSON-lines wrapper around Jiten.Parser.Parser's real parsing stages, with a local JMdict lookup index.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jiten.Parser;

namespace Jiten.TokenizerHelper;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task Main(string[] args)
    {
        // GSM_JITEN_UTF8_CONSOLE_IO_V1
        // GSM Overlay's Node bridge decodes child stdout as UTF-8. Pin both redirected
        // streams to UTF-8 before capturing Console.Out for the JSON-lines protocol;
        // otherwise Windows may encode Japanese JSON using a legacy console code page.
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.Error.WriteLine($"[JitenOffline JMdict] stdin encoding={Console.InputEncoding.WebName}; stdout encoding={Console.OutputEncoding.WebName}");

        // stdout is the JSON-lines protocol. Parser diagnostics are kept on stderr.
        var protocolOutput = Console.Out;
        Console.SetOut(Console.Error);

        var baseDirectory = AppContext.BaseDirectory;
        var resourcesDirectory = Path.Combine(baseDirectory, "resources");
        var dictionaryPath = Path.Combine(resourcesDirectory, "system.dic");
        if (File.Exists(dictionaryPath))
        {
            Environment.SetEnvironmentVariable("DictionaryPath", dictionaryPath);
            Console.Error.WriteLine($"[JitenOffline JMdict] DictionaryPath={dictionaryPath}");
        }
        else
        {
            Console.Error.WriteLine($"[JitenOffline JMdict] WARNING: Sudachi dictionary not found: {dictionaryPath}");
        }

        string? jmDictPath = ResolveResourcePath(
            Environment.GetEnvironmentVariable("JITEN_JMDICT_PATH"),
            Path.Combine(resourcesDirectory, "JMdict_e.xml"),
            Path.Combine(resourcesDirectory, "JMdict.xml"),
            Path.Combine(baseDirectory, "JMdict_e.xml"),
            Path.Combine(baseDirectory, "JMdict.xml"));

        string? furiganaPath = null; // Optional partial-kana resource is disabled in this build.

        if (jmDictPath == null)
            Console.Error.WriteLine("[JitenOffline JMdict] ERROR: No JMdict XML found. Set JITEN_JMDICT_PATH or put JMdict_e.xml in the resources folder.");
        else
            Console.Error.WriteLine($"[JitenOffline JMdict] JMdict XML={jmDictPath}");
        Console.Error.WriteLine("[JitenOffline JMdict] Using JMdict XML only; optional partial-kana spelling recovery is disabled in this build.");

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var request = JsonDocument.Parse(line);
                if (!request.RootElement.TryGetProperty("text", out var textValue)
                    || textValue.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Request must contain a string property named 'text'.");
                if (jmDictPath == null)
                    throw new FileNotFoundException("JMdict XML is required. Set JITEN_JMDICT_PATH or put JMdict_e.xml in resources.");

                var sourceText = textValue.GetString() ?? string.Empty;
                int searchFrom = 0;
                (int Start, int End)? LocateSpan(string surface) => TryFindSourceSpan(sourceText, surface, ref searchFrom);

                var tokens = await global::Jiten.Parser.Parser.ParseOverlayOfflineOutputAsync(
                    sourceText, jmDictPath, furiganaPath, LocateSpan, diagnostics: null);
                protocolOutput.WriteLine(JsonSerializer.Serialize(new { tokens }, JsonOptions));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[JitenOffline JMdict] Parse failed: " + ex);
                protocolOutput.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            }
        }
    }

    private static string? ResolveResourcePath(string? environmentPath, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(environmentPath.Trim().Trim('"'));
            var fullPath = Path.GetFullPath(expanded);
            return File.Exists(fullPath) ? fullPath : null;
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static (int Start, int End)? TryFindSourceSpan(string source, string surface, ref int searchFrom)
    {
        if (surface.Length == 0 || source.Length == 0) return null;
        searchFrom = Math.Clamp(searchFrom, 0, source.Length);

        var exact = source.IndexOf(surface, searchFrom, StringComparison.Ordinal);
        if (exact >= 0)
        {
            searchFrom = exact + surface.Length;
            return (exact, searchFrom);
        }

        // Only a fallback for Unicode compatibility differences. Returned offsets always refer
        // to the unchanged original source string used by the overlay.
        string normalizedSurface;
        try { normalizedSurface = surface.Normalize(NormalizationForm.FormKC); }
        catch { return null; }

        int minimumLength = Math.Max(1, surface.Length - 3);
        int maximumLength = Math.Min(source.Length - searchFrom, surface.Length + 3);
        for (int candidate = searchFrom; candidate < source.Length; candidate++)
        {
            int localMaximum = Math.Min(maximumLength, source.Length - candidate);
            for (int length = minimumLength; length <= localMaximum; length++)
            {
                string normalized;
                try { normalized = source.Substring(candidate, length).Normalize(NormalizationForm.FormKC); }
                catch { continue; }
                if (!string.Equals(normalized, normalizedSurface, StringComparison.Ordinal)) continue;
                searchFrom = candidate + length;
                return (candidate, searchFrom);
            }
        }
        return null;
    }
}
