// GSM_JITEN_OFFLINE_JMDICT_PIPELINE_V1
// GSM_JITEN_OFFLINE_JMDICT_COMPILEFIX_V1
// Offline entry point that reuses Parser.ParseText's real pipeline, backed by JMdict XML.
using System.Collections.Concurrent;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Utils;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Resegmentation;
using Jiten.Parser.Scoring;

using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace Jiten.Parser;

public static partial class Parser
{
    private static readonly SemaphoreSlim OfflineInitGate = new(1, 1);
    private static string? _offlineJmDictPath;
    private static bool _offlineInitialized;
    // GSM_JITEN_OFFLINE_JMDICT_POS_TAG_CODES_V1
    // JMdict's DTD expands tags such as &n; into display descriptions. The parser
    // lookup model expects the canonical entity keys (n, vs, vt, uk, arch, ...).
    private static Dictionary<string, string> _offlineEntityCodeByDescription = new(StringComparer.Ordinal);
    private static string? _offlineFuriganaPath;
    // GSM_JITEN_OFFLINE_JMNEDICT_V1
    private static string? _offlineJmnedictPath;


    private sealed record OfflineReading(string Text, bool NoKanji, string[] Restrictions, string[] Priorities, string[] Info);
    private sealed record OfflineKanji(string Text, string[] Priorities, string[] Info);

    private sealed class OfflineEntry
    {
        public required int Id { get; init; }
        public required List<OfflineKanji> Kanji { get; init; }
        public required List<OfflineReading> Readings { get; init; }
        public required string[] PosTags { get; init; }
        public required string[] Priorities { get; init; }
        public required bool IsUsuallyKana { get; init; }
        public required bool IsFullyArchaic { get; init; }
        public required JmDictWord Word { get; init; }
        public bool IsNameEntry { get; init; }
    }

    /// <summary>Resolve an offline parser token to its canonical form and reading for overlay JSON.</summary>
    public static async Task<List<object>> ParseOverlayOfflineOutputAsync(string text, string jmDictXmlPath,
        string? jmDictFuriganaJsonPath, Func<string, (int Start, int End)?> locateSourceSpan, ParserDiagnostics? diagnostics = null)
    {
        var parsed = await ParseTextOfflineResolvedAsync(text, jmDictXmlPath, jmDictFuriganaJsonPath, diagnostics);
        OfflineTrace("10 Final DeckWord results before JSON/source mapping", parsed);
        var output = new List<object>(parsed.Count);
        var dictionaryWords = parsed.Count == 0
            ? new Dictionary<int, JmDictWord>()
            : await GetWordsWithCache(parsed.Select(p => p.WordId).Distinct(), null);
        OfflineTrace("11 Dictionary entries retrieved for final output", dictionaryWords);
        int searchFrom = 0;
        foreach (var item in parsed)
        {
            var surfaceHint = item.OriginalText ?? string.Empty;
            if (surfaceHint.Length == 0) continue;
            var span = locateSourceSpan(surfaceHint);
            if (span == null || span.Value.Start < searchFrom) continue;
            int start = span.Value.Start;
            int end = span.Value.End;
            if (start < 0 || end <= start || end > text.Length) continue;
            string surface = text[start..end];

            if (!dictionaryWords.TryGetValue(item.WordId, out var entry)) continue;
            var sameReading = entry.Forms.Where(f => f.ReadingIndex == item.ReadingIndex
                    && f.IsActiveInLatestSource && !f.IsObsolete && !f.IsSearchOnly).ToList();
            if (sameReading.Count == 0)
                sameReading = entry.Forms.Where(f => f.ReadingIndex == item.ReadingIndex).ToList();

            var matchingSurface = sameReading.FirstOrDefault(f => string.Equals(f.Text, surface, StringComparison.Ordinal));
            var kanaForm = sameReading.FirstOrDefault(f => f.FormType == JmDictFormType.KanaForm);
            var kanjiForm = sameReading.FirstOrDefault(f => f.FormType == JmDictFormType.KanjiForm);
            var headword = matchingSurface?.Text ?? kanjiForm?.Text ?? kanaForm?.Text ?? surface;
            var reading = kanaForm?.Text;
            if (string.IsNullOrWhiteSpace(reading)) reading = surface;

            output.Add(new
            {
                start, end, word = surface, headword, reading,
                knownState = new[] { 0 }, readingIndex = (int)item.ReadingIndex, wordId = item.WordId
            });
            searchFrom = end;
        }
        OfflineTrace("12 Final JSON overlay tokens", output);
        return output;
    }

    // Same Parser.ParseText flow, split from the JSON surface mapping so it can be unit-tested independently.
    private static async Task<List<DeckWord>> ParseTextOfflineResolvedAsync(string text, string jmDictXmlPath,
        string? jmDictFuriganaJsonPath, ParserDiagnostics? diagnostics)
    {
        await EnsureOfflineInitializedAsync(jmDictXmlPath, jmDictFuriganaJsonPath);
        var (cleanText, furiganaHints) = FuriganaHintExtractor.Extract(text);
        var parser = new MorphologicalAnalyser
        {
            Interactive = true, HasCompoundLookup = HasLookupForCompound, HasNonNameCompoundLookup = HasNonNameLookup,
            HasPrioritizedNonNameCompoundLookup = HasPrioritizedNonNameLookup, HasKanaAppropriateCompoundLookup = HasKanaAppropriateLookup,
            HasSuruVerbCompoundLookup = HasSuruVerbLookup, GetNonNameCompoundWordId = GetNonNameCompoundId,
            GetNonNameCompoundFrequencyRank = GetBestNonNameFrequencyRank, HasVerbOrAdjectiveLookup = HasVerbOrAdjectiveLookup,
            HasExpressionLookup = HasExpressionLookup, HasCounterSenseLookup = HasCounterSenseAvailable,
            GetPartialKanaSpelling = GetPartialKanaSpelling
        };
        var (sentences, cleanedOriginal) = await parser.ParseWithCleanedOriginal(cleanText, preserveStopToken: false, diagnostics: diagnostics);
        OfflineTrace("01 After MorphologicalAnalyser.ParseWithCleanedOriginal", sentences);
        var cleanedOriginalFlat = cleanedOriginal.Replace("\r", "").Replace("\n", "");
        FuriganaHint[]? relocatedHints = furiganaHints.Length > 0 && cleanedOriginalFlat.Length > 0
            ? FuriganaHintExtractor.RelocateToCleanedOriginal(cleanedOriginalFlat, furiganaHints, cleanText) : null;

        PreprocessSentences(sentences, diagnostics);
        OfflineTrace("02 After PreprocessSentences", sentences);
        MergeConfirmedNameSplits([sentences]);
        PropagatePersonNameContexts(sentences);
        PinBareIchidanStemsBeforeAuxiliaryVerbs(sentences, diagnostics);
        OfflineTrace("03 After MergeConfirmedNameSplits + PropagatePersonNameContexts + auxiliary-verb stem pinning", sentences);
        var wordInfos = ExtractWordInfos(sentences);
        OfflineTrace("04 Extracted WordInfo tokens", wordInfos);
        var processedWithMargins = await ProcessWordsInBatches(wordInfos, Deconjugator.Instance, diagnostics: diagnostics);
        OfflineTrace("05 Initial dictionary/deconjugation results", processedWithMargins);
        await OfflineTraceResolutionDetails(wordInfos, processedWithMargins);
        var marginMap = BuildMarginMap(sentences, processedWithMargins);
        var candidateLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), List<FormCandidate>>();
        var offlineDidResegment = ResegmentationEngine.TryResegmentLowConfidenceTokens(sentences, _lookups, _wordFrequencyRanks, marginMap, WordMeta, diagnostics);
        OfflineTrace("06 Resegmentation triggered=" + offlineDidResegment, sentences);
        if (offlineDidResegment)
        {
            diagnostics?.Results.Clear();
            var oldResultLookup = new Dictionary<(string, PartOfSpeech, string, string, bool, bool, int?), (DeckWord? word, int? margin)>();
            for (int i = 0; i < wordInfos.Count; i++)
            {
                var key = GetDedupKey(wordInfos[i]);
                oldResultLookup.TryAdd(key, (processedWithMargins[i].word, processedWithMargins[i].margin));
                if (processedWithMargins[i].candidates != null) candidateLookup.TryAdd(key, processedWithMargins[i].candidates!);
            }
            wordInfos = ExtractWordInfos(sentences);
            var newTokens = new List<WordInfo>();
            foreach (var wi in wordInfos) if (!oldResultLookup.ContainsKey(GetDedupKey(wi))) newTokens.Add(wi);
            if (newTokens.Count > 0)
            {
                var newResults = await ProcessWordsInBatches(newTokens, Deconjugator.Instance, diagnostics: diagnostics);
                for (int i = 0; i < newTokens.Count; i++)
                {
                    var key = GetDedupKey(newTokens[i]);
                    oldResultLookup.TryAdd(key, (newResults[i].word, newResults[i].margin));
                    if (newResults[i].candidates != null) candidateLookup.TryAdd(key, newResults[i].candidates!);
                }
            }
            processedWithMargins = wordInfos.Select(wi =>
            {
                var key = GetDedupKey(wi);
                if (oldResultLookup.TryGetValue(key, out var r)) return (r.word, r.margin, candidateLookup.GetValueOrDefault(key));
                return ((DeckWord?)null, (int?)null, (List<FormCandidate>?)null);
            }).ToList();
        }
        else
        {
            for (int i = 0; i < wordInfos.Count; i++)
                if (processedWithMargins[i].candidates != null)
                    candidateLookup.TryAdd(GetDedupKey(wordInfos[i]), processedWithMargins[i].candidates!);
        }
        OfflineTrace("07 Tokens after resegmentation decision", wordInfos);
        OfflineTrace("08 Results after resegmentation decision", processedWithMargins);
        var corrected = await ApplyAdjacentScoring(sentences, processedWithMargins, candidateLookup, diagnostics, relocatedHints);
        OfflineTrace("09 After ApplyAdjacentScoring", corrected);
        corrected = await ApplyMisparseGates(sentences, corrected, diagnostics);
        OfflineTrace("09b After ApplyMisparseGates", corrected);
        var finalWords = ExcludeFinalMisparses(corrected, diagnostics).ToList();
        OfflineTrace("09c After ExcludeFinalMisparses", finalWords);
        return finalWords;
    }


    // Sudachi can emit a bare ichidan stem as a standalone Verb immediately before an
    // auxiliary verb marked PossibleDependant (e.g. 付け|始めた). In this construction,
    // use Jiten's existing VerbStemLookup to pin the stem to its attested surface+る entry.
    // Keep Sudachi's original POS, DictionaryForm and Reading; do not route it through the
    // noun-versus-verb arbitration, which lacks the following auxiliary's context.
    private static void PinBareIchidanStemsBeforeAuxiliaryVerbs(
        List<SentenceInfo> sentences, ParserDiagnostics? diagnostics)
    {
        static bool IsBareIchidanVerbStem(WordInfo word) =>
            word.PartOfSpeech == PartOfSpeech.Verb
            && word.Text.Length > 0
            && word.Text.Any(JapaneseTextHelper.IsKanji)
            && string.Equals(word.DictionaryForm, word.Text + "る", StringComparison.Ordinal);

        bool HasAuxiliaryVerbEntry(string dictionaryForm)
        {
            if (string.IsNullOrWhiteSpace(dictionaryForm)
                || !_lookups.TryGetValue(dictionaryForm, out var ids)
                || ids == null || ids.Count == 0)
                return false;

            WordMeta.PrefetchWordMetadata(ids);
            return ids.Any(id => WordMeta.TryGetValue(id, out var meta)
                                 && meta.Pos.Contains(PartOfSpeech.Auxiliary));
        }

        foreach (var sentence in sentences)
        {
            var words = sentence.Words;
            for (int i = 0; i + 1 < words.Count; i++)
            {
                var (stem, position, length) = words[i];
                var (following, followingPosition, _) = words[i + 1];

                if (!IsBareIchidanVerbStem(stem)
                    || stem.PreMatchedWordId.HasValue
                    || stem.PreMatchedCandidateWordIds != null
                    || string.IsNullOrWhiteSpace(stem.Reading)
                    || position < 0 || length <= 0 || position + length != followingPosition
                    || following.PartOfSpeech != PartOfSpeech.Verb
                    || !(following.HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant)
                         || following.HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem))
                    || !HasAuxiliaryVerbEntry(following.DictionaryForm))
                    continue;

                // Find() verifies both the surface+る dictionary entry and its reading (ツケ → つける).
                // Pin() is the same helper used by Jiten's online compound-verb preprocessing.
                var verb = VerbStemLookup.Find(stem.Text, stem.Reading, _lookups, WordMeta, _wordFrequencyRanks);
                if (verb is not { } match
                    || !string.Equals(match.DictionaryForm, stem.Text + "る", StringComparison.Ordinal))
                    continue;

                VerbStemLookup.Pin(stem, match);
                diagnostics?.LogParserEvent(
                    "PinBareIchidanStemsBeforeAuxiliaryVerbs", "pin",
                    [stem.Text, following.Text], [stem.Text],
                    $"attested ichidan stem before auxiliary verb {following.DictionaryForm} " +
                    $"(DictionaryForm={match.DictionaryForm}, WordId={match.WordId})");
            }
        }
    }


// GSM_JITEN_OFFLINE_PIPELINE_TRACE_V1

    private static void OfflineTrace(string stage, object? value)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JITEN_OFFLINE_TRACE"), "1", StringComparison.Ordinal))
            return;

        Console.Error.WriteLine($"[JitenOffline Trace] {stage}");
        if (value == null)
        {
            Console.Error.WriteLine("  <null>");
            return;
        }
        if (value is string text)
        {
            Console.Error.WriteLine("  " + text);
            return;
        }
        if (value is System.Collections.IDictionary dictionary)
        {
            int count = 0;
            foreach (System.Collections.DictionaryEntry pair in dictionary)
            {
                if (count++ >= 30) { Console.Error.WriteLine("  ... (dictionary output truncated)"); break; }
                Console.Error.WriteLine($"  {pair.Key}: {OfflineTraceDescribe(pair.Value, 0)}");
            }
            Console.Error.WriteLine($"  entries shown: {Math.Min(count, 30)} / {dictionary.Count}");
            return;
        }
        if (value is System.Collections.IEnumerable sequence)
        {
            int count = 0;
            foreach (var item in sequence)
            {
                if (count >= 100) { Console.Error.WriteLine("  ... (items truncated at 100)"); break; }
                Console.Error.WriteLine($"  [{count}] {OfflineTraceDescribe(item, 0)}");
                count++;
            }
            Console.Error.WriteLine($"  items shown: {count}");
            return;
        }
        Console.Error.WriteLine("  " + OfflineTraceDescribe(value, 0));
    }

    private static string OfflineTraceDescribe(object? value, int depth)
    {
        if (value == null) return "<null>";
        if (value is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Text", "OriginalText", "DictionaryForm", "NormalizedForm", "Reading", "SuffixReading", "PartOfSpeech", "Pos",
            "PartOfSpeechSection1", "PartOfSpeechSection2", "PartOfSpeechSection3",
            "WordId", "ReadingIndex", "PreMatchedWordId", "PreMatchedCandidateWordIds", "PreMatchedConjugations", "Start", "End", "Surface", "Word", "Margin", "Score", "Reason",
            "IsMisparse", "IsName", "Conjugations", "Key", "Value", "word", "margin", "candidates",
            "Words", "Tokens", "IsKana", "OriginalReading", "Form", "NormalizedText"
        };
        var parts = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!wanted.Contains(property.Name) || property.GetIndexParameters().Length != 0) continue;
            object? propertyValue;
            try { propertyValue = property.GetValue(value); }
            catch { continue; }
            if (propertyValue == null) { parts.Add(property.Name + "=<null>"); continue; }

            if (propertyValue is string || propertyValue.GetType().IsPrimitive || propertyValue.GetType().IsEnum || propertyValue is decimal)
            {
                parts.Add(property.Name + "=" + OfflineTraceDescribe(propertyValue, depth + 1));
                continue;
            }
            if (propertyValue is System.Collections.IEnumerable enumerable && propertyValue is not string)
            {
                var nested = enumerable.Cast<object?>().Take(depth < 1 ? 8 : 3).ToList();
                string summary = property.Name + "[" + string.Join(", ", nested.Select(x => OfflineTraceDescribe(x, depth + 1))) + (nested.Count == (depth < 1 ? 8 : 3) ? ", …" : "") + "]";
                if (depth < 1 || property.Name.Equals("Words", StringComparison.OrdinalIgnoreCase)) parts.Add(summary);
                else parts.Add(property.Name + "=<collection>");
                continue;
            }
            if (depth < 1 && (property.Name.Equals("Word", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("Value", StringComparison.OrdinalIgnoreCase)))
                parts.Add(property.Name + "=" + OfflineTraceDescribe(propertyValue, depth + 1));
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!wanted.Contains(field.Name)) continue;
            object? fieldValue;
            try { fieldValue = field.GetValue(value); }
            catch { continue; }
            if (fieldValue == null || fieldValue is string || fieldValue.GetType().IsPrimitive || fieldValue.GetType().IsEnum || fieldValue is decimal)
                parts.Add(field.Name + "=" + OfflineTraceDescribe(fieldValue, depth + 1));
            else if (depth < 1 && field.Name.Equals("word", StringComparison.OrdinalIgnoreCase))
                parts.Add(field.Name + "=" + OfflineTraceDescribe(fieldValue, depth + 1));
            else if (depth < 1 && field.Name.Equals("candidates", StringComparison.OrdinalIgnoreCase) && fieldValue is System.Collections.IEnumerable enumerable)
                parts.Add(field.Name + "[" + string.Join(", ", enumerable.Cast<object?>().Take(3).Select(x => OfflineTraceDescribe(x, depth + 1))) + ", …]");
        }
        return type.Name + (parts.Count == 0 ? "" : " {" + string.Join("; ", parts.Distinct()) + "}");
    }

    // GSM_JITEN_OFFLINE_RESOLUTION_TRACE_V2
    private static async Task OfflineTraceResolutionDetails(
        List<WordInfo> wordInfos,
        List<(DeckWord? word, int? margin, List<FormCandidate>? candidates)> results)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JITEN_OFFLINE_TRACE"), "1", StringComparison.Ordinal))
            return;

        Console.Error.WriteLine("[JitenOffline Trace V2] Detailed word-resolution results");
        int count = Math.Min(wordInfos.Count, results.Count);
        for (int i = 0; i < count; i++)
        {
            var wi = wordInfos[i];
            var result = results[i];
            Console.Error.WriteLine(
                $"  [{i}] text=\"{wi.Text}\" pos={wi.PartOfSpeech} normalized=\"{wi.NormalizedForm}\" dictionary=\"{wi.DictionaryForm}\" reading=\"{wi.Reading}\" => wordId={(result.word == null ? "<null>" : result.word.WordId.ToString())} margin={(result.margin == null ? "<null>" : result.margin.ToString())} candidates={(result.candidates == null ? "<null>" : result.candidates.Count.ToString())}");

            if (result.candidates != null)
            {
                foreach (var c in result.candidates.Take(8))
                {
                    string deconj = c.DeconjForm == null ? "-" : c.DeconjForm.Text;
                    string tags = c.DeconjForm == null ? "-" : string.Join(",", c.DeconjForm.Tags);
                    string chain = c.DeconjForm == null ? "-" : string.Join(">", c.DeconjForm.Process);
                    Console.Error.WriteLine(
                        $"      candidate id={c.Word.WordId} form=\"{c.Form.Text}\" type={c.Form.FormType} readingIndex={c.ReadingIndex} target=\"{c.TargetHiragana}\" pos=[{string.Join(",", c.Word.PartsOfSpeech)}] cachedPos=[{string.Join(",", c.Word.CachedPOS)}] score={c.TotalScore} deconj=\"{deconj}\" tags=[{tags}] process=[{chain}]");
                }
                if (result.candidates.Count > 8)
                    Console.Error.WriteLine($"      ... {result.candidates.Count - 8} more candidates omitted");
            }

            if (result.word != null || !(wi.PartOfSpeech is PartOfSpeech.Verb or PartOfSpeech.IAdjective
                or PartOfSpeech.Auxiliary or PartOfSpeech.NaAdjective or PartOfSpeech.Expression))
                continue;

            // Reproduce CollectVerbAdjectiveCandidates' normalized input and exact dictionary-key probe.
            string normalizedText = KanaNormalizer.Normalize(KanaConverter.ToHiragana(wi.Text));
            Console.Error.WriteLine($"      unresolved verb/adjective normalizedInput=\"{normalizedText}\"");
            var forms = Deconjugator.Instance.Deconjugate(normalizedText).ToList();
            int formsShown = 0;
            foreach (var form in forms)
            {
                if (formsShown++ >= 30)
                {
                    Console.Error.WriteLine("      ... deconjugation forms truncated at 30");
                    break;
                }

                _lookups.TryGetValue(form.Text, out var ids);
                string idSummary = ids == null || ids.Count == 0 ? "<no exact lookup key>" : string.Join(",", ids.Take(12));
                Console.Error.WriteLine(
                    $"      deconj text=\"{form.Text}\" tags=[{string.Join(",", form.Tags)}] process=[{string.Join(">", form.Process)}] lookupIds=[{idSummary}]");

                if (ids == null || ids.Count == 0) continue;
                var smallIds = ids.Take(4).ToArray();
                Dictionary<int, JmDictWord> entries;
                try { entries = await GetWordsWithCache(smallIds, null); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"        cache probe failed: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                foreach (var id in smallIds)
                {
                    if (!entries.TryGetValue(id, out var entry))
                    {
                        Console.Error.WriteLine($"        id={id}: not returned by JMdict cache");
                        continue;
                    }
                    string meta = WordMeta.TryGetValue(id, out var m)
                        ? $"metaPos=[{string.Join(",", m.Pos)}] kanaScore={m.PriorityScoreKana} kanjiScore={m.PriorityScoreKanji} trueName={m.IsTrueName}"
                        : "meta=<missing>";
                    string entryForms = string.Join("; ", entry.Forms.Take(12).Select(f =>
                        $"{f.Text}({f.FormType},ri={f.ReadingIndex},obsolete={f.IsObsolete},searchOnly={f.IsSearchOnly})"));
                    Console.Error.WriteLine(
                        $"        id={id} tags=[{string.Join(",", entry.PartsOfSpeech)}] cachedPos=[{string.Join(",", entry.CachedPOS)}] {meta} forms=[{entryForms}]");
                }
            }
        }
    }

    // GSM_JITEN_OFFLINE_JMDICT_POS_TAG_CODES_V1
    private static Dictionary<string, string> ParseOfflineEntityCodeByDescription(string internalSubset)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(internalSubset)) return result;

        // Only general, quoted entity declarations are wanted; parameter entities
        // and external SYSTEM/PUBLIC declarations do not supply a simple tag value.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            internalSubset,
            @"<!ENTITY\s+(?!%)\s*(?<name>[A-Za-z][A-Za-z0-9._:-]*)\s+(?:""(?<double>[^""]*)""|'(?<single>[^']*)')\s*>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            string name = match.Groups["name"].Value;
            string value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Value;
            if (name.Length > 0 && value.Length > 0 && !result.ContainsKey(value))
                result.Add(value, name);
        }
        return result;
    }

    private static string NormalizeOfflineJmDictTagValue(string elementName, string value)
    {
        // These JMdict elements use DTD entity codes whose expanded descriptions
        // must be converted back to the canonical codes that Jiten stores in its DB.
        bool isTagValue = elementName == "pos" || elementName == "misc"
            || elementName == "field" || elementName == "dial"
            || elementName == "ke_inf" || elementName == "re_inf";
        if (!isTagValue) return value;

        return _offlineEntityCodeByDescription.TryGetValue(value, out var code) ? code : value;
    }

    // GSM_JITEN_OFFLINE_SQLITE_CACHE_V1_2
    // GSM_JITEN_OFFLINE_SQLITE_INDEX_V1
// GSM_JITEN_OFFLINE_SQLITE_KANA_ALIAS_FIX_V1_1
    private static async Task EnsureOfflineInitializedAsync(string jmDictXmlPath, string? jmDictFuriganaJsonPath)
    {
        var fullPath = Path.GetFullPath(jmDictXmlPath);
        string? furiganaFullPath = string.IsNullOrWhiteSpace(jmDictFuriganaJsonPath) || !File.Exists(jmDictFuriganaJsonPath)
            ? null : Path.GetFullPath(jmDictFuriganaJsonPath);
        string? jmnedictFullPath = ResolveOfflineJmnedictPath(fullPath);
        if (_offlineInitialized)
        {
            if (!string.Equals(_offlineJmDictPath, fullPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_offlineFuriganaPath, furiganaFullPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_offlineJmnedictPath, jmnedictFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This helper process is already initialized with different dictionary resource paths.");
            return;
        }

        await OfflineInitGate.WaitAsync();
        try
        {
            if (_offlineInitialized)
            {
                if (!string.Equals(_offlineJmDictPath, fullPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_offlineFuriganaPath, furiganaFullPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_offlineJmnedictPath, jmnedictFullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This helper process is already initialized with different dictionary resource paths.");
                return;
            }
            if (!File.Exists(fullPath)) throw new FileNotFoundException("JMdict XML was not found. Set JITEN_JMDICT_PATH or place JMdict_e.xml in the helper resources folder.", fullPath);

            string jmDictHash = OfflineFileHash(fullPath);
            string jmnedictHash = OfflineFileHash(jmnedictFullPath);
            var databasePath = ResolveOfflineCacheDatabasePath(jmDictHash, jmnedictHash);
            using var connection = OpenOfflineCacheConnection(databasePath);
            EnsureOfflineCacheSchema(connection);

            int entryCount;
            int jmnedictEntryCount;

            if (OfflineCacheMatches(connection, jmDictHash, jmnedictHash))
            {
                entryCount = CountOfflineRecords(connection, isName: false);
                jmnedictEntryCount = CountOfflineRecords(connection, isName: true);
                var lookupCount = CountOfflineLookupForms(connection);
                Console.Error.WriteLine($"[JitenOffline SQLite] Reused SQLite dictionary index: {entryCount:N0} JMdict entries, {jmnedictEntryCount:N0} JMnedict entries, {lookupCount:N0} lookup forms.");
            }
            else
            {
                Console.Error.WriteLine($"[JitenOffline SQLite] Building local SQLite dictionary cache at {databasePath}. This is a one-time operation for these XML files.");
                BuildOfflineCacheFromXml(connection, fullPath, jmnedictFullPath, jmDictHash, jmnedictHash,
                    out entryCount, out jmnedictEntryCount);
                var lookupCount = CountOfflineLookupForms(connection);
                Console.Error.WriteLine($"[JitenOffline SQLite] Built SQLite cache: {entryCount:N0} JMdict entries, {jmnedictEntryCount:N0} JMnedict entries, {lookupCount:N0} lookup forms.");
            }

            // This checkout explicitly has no compatible PartialKanaSpellings builder;
            // preserve its existing behavior and leave partial-kana recovery disabled.
            var partialKanaSpellings = new Dictionary<string, Data.PartialKanaForm?>();
            if (furiganaFullPath != null)
                Console.Error.WriteLine("[JitenOffline SQLite] A furigana JSON path was supplied, but partial-kana recovery is disabled in this source build.");
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            int lookupCacheEntries = ReadPositiveEnvironmentInt("JITEN_LOOKUP_CACHE_ENTRIES", 50000);
            int metadataCacheEntries = ReadPositiveEnvironmentInt("JITEN_METADATA_CACHE_ENTRIES", 50000);
            var offlineIndex = new OfflineSqliteDictionaryIndex(databasePath, lookupCacheEntries, metadataCacheEntries);
            _lookups = offlineIndex;
            _wordFrequencyRanks = offlineIndex;
            WordMeta = offlineIndex;
            _partialKanaSpellings = partialKanaSpellings;
            WordFrequencyPriors.Current = new Dictionary<int, double>(); // JMdict XML has no usage-observation frequencies.
            DeckWordCache = new OfflineDeckWordCache();
            JmDictCache = new OfflineJmDictCache(databasePath);
            _contextFactory = null!; // ParseText's token-only path never queries form-frequency tables.
            _compoundHashSet = null;
            BuildCompoundHashSet();
            _offlineJmDictPath = fullPath;
            _offlineFuriganaPath = furiganaFullPath;
            _offlineJmnedictPath = jmnedictFullPath;
            _offlineInitialized = true;
            Console.Error.WriteLine($"[JitenOffline SQLite] Database={databasePath}; partial-kana forms: {_partialKanaSpellings.Count:N0}; hydrated-word cache limit: {OfflineJmDictCache.MaxCachedWords:N0}.");
        }
        finally
        {
            OfflineInitGate.Release();
        }
    }

    private sealed record OfflineStoredForm(
        string Text, int ReadingIndex, string FormType, string[] Priorities,
        bool IsActiveInLatestSource, bool IsObsolete, bool IsSearchOnly);

    private sealed record OfflineStoredWord(
        string[] PosTags, string[] Priorities, bool IsUsuallyKana, bool IsFullyArchaic,
        bool IsNameEntry, OfflineStoredForm[] Forms);

    private sealed record OfflineWordMetaPayload(
        string[] PosTags, short KanaScore, short KanjiScore, string Origin, bool HasTrueName);

    private static string ResolveOfflineCacheDatabasePath(string jmDictHash, string jmnedictHash)
    {
        var configured = Environment.GetEnvironmentVariable("JITEN_OFFLINE_CACHE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured);
            Directory.CreateDirectory(Path.GetDirectoryName(configuredPath)!);
            return configuredPath;
        }

        var identity = jmDictHash + "\n" + jmnedictHash;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..20];
        var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GSM-JitenTokenizer", "DictionaryCache");
        Directory.CreateDirectory(cacheDirectory);
        return Path.Combine(cacheDirectory, $"offline-dictionary-{digest}.sqlite");
    }

    private static SqliteConnection OpenOfflineCacheConnection(string path)
    {
        SqliteConnection connection = null!;
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
            connection.Open();
            EnsureOfflineCacheSchema(connection);
            return connection;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT / SQLITE_NOTADB only
        {
            try { connection?.Dispose(); } catch { }
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { var damaged = path + suffix; if (File.Exists(damaged)) File.Delete(damaged); } catch { }
            }
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
            connection.Open();
            EnsureOfflineCacheSchema(connection);
            return connection;
        }
    }

    private static void EnsureOfflineCacheSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
PRAGMA busy_timeout=10000;
PRAGMA temp_store=MEMORY;
PRAGMA cache_size=-8192;
CREATE TABLE IF NOT EXISTS cache_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS word_records (word_id INTEGER PRIMARY KEY, is_name INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS lookup_forms (form TEXT NOT NULL, word_id INTEGER NOT NULL, ordinal INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(form, word_id));
CREATE TABLE IF NOT EXISTS word_index (word_id INTEGER PRIMARY KEY, frequency_rank INTEGER NOT NULL, is_name_only INTEGER NOT NULL, is_expression INTEGER NOT NULL, is_kanji_backed INTEGER NOT NULL, meta_payload TEXT NOT NULL);";
        command.ExecuteNonQuery();
        bool hasOrdinal = false;
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(lookup_forms);";
            using var reader = columns.ExecuteReader();
            while (reader.Read()) if (string.Equals(reader.GetString(1), "ordinal", StringComparison.OrdinalIgnoreCase)) hasOrdinal = true;
        }
        if (!hasOrdinal)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE lookup_forms ADD COLUMN ordinal INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_lookup_forms_form_ordinal ON lookup_forms(form, ordinal);";
            index.ExecuteNonQuery();
        }
    }

    private static string OfflineFileHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "<none>";
        if (!File.Exists(path)) return "<missing>";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool OfflineCacheMatches(SqliteConnection connection, string jmDictHash, string jmnedictHash)
    {
        try
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema"] = "4",
                ["jmdict"] = jmDictHash,
                ["jmnedict"] = jmnedictHash
            };
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM cache_info WHERE key=$key;";
            var keyParameter = command.CreateParameter(); keyParameter.ParameterName = "$key"; command.Parameters.Add(keyParameter);
            foreach (var pair in expected)
            {
                keyParameter.Value = pair.Key;
                var value = command.ExecuteScalar() as string;
                if (!string.Equals(value, pair.Value, StringComparison.Ordinal)) return false;
            }
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM word_index;";
            return Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        catch { return false; }
    }

    private static void StoreOfflineCacheInfo(SqliteConnection connection, SqliteTransaction transaction,
        string jmDictHash, string jmnedictHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO cache_info(key,value) VALUES ($key,$value);";
        var key = command.CreateParameter(); key.ParameterName = "$key"; command.Parameters.Add(key);
        var value = command.CreateParameter(); value.ParameterName = "$value"; command.Parameters.Add(value);
        void Put(string k, string v) { key.Value = k; value.Value = v; command.ExecuteNonQuery(); }
        Put("schema", "4");
        Put("jmdict", jmDictHash);
        Put("jmnedict", jmnedictHash);
    }

    private static int CountOfflineLookupForms(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT form) FROM lookup_forms;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadPositiveEnvironmentInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed >= 128 ? parsed : fallback;
    }



    private static int CountOfflineRecords(SqliteConnection connection, bool isName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM word_records WHERE is_name=$name;";
        command.Parameters.AddWithValue("$name", isName ? 1 : 0);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void BuildOfflineCacheFromXml(SqliteConnection connection, string jmDictPath, string? jmnedictPath,
        string jmDictHash, string jmnedictHash,
        out int entryCount, out int jmnedictEntryCount)
    {
        entryCount = 0;
        jmnedictEntryCount = 0;
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM cache_info; DELETE FROM lookup_forms; DELETE FROM word_index; DELETE FROM word_records;";
            clear.ExecuteNonQuery();
        }

        using var wordInsert = connection.CreateCommand();
        wordInsert.Transaction = transaction;
        wordInsert.CommandText = "INSERT OR REPLACE INTO word_records(word_id,is_name,payload) VALUES($id,$isName,$payload);";
        wordInsert.Parameters.Add("$id", SqliteType.Integer);
        wordInsert.Parameters.Add("$isName", SqliteType.Integer);
        wordInsert.Parameters.Add("$payload", SqliteType.Text);
        wordInsert.Prepare();

        using var lookupInsert = connection.CreateCommand();
        lookupInsert.Transaction = transaction;
        lookupInsert.CommandText = "INSERT OR IGNORE INTO lookup_forms(form,word_id,ordinal) VALUES($form,$id,$ordinal);";
        lookupInsert.Parameters.Add("$form", SqliteType.Text);
        lookupInsert.Parameters.Add("$id", SqliteType.Integer);
        lookupInsert.Parameters.Add("$ordinal", SqliteType.Integer);
        lookupInsert.Prepare();

        using var indexInsert = connection.CreateCommand();
        indexInsert.Transaction = transaction;
        indexInsert.CommandText = "INSERT OR REPLACE INTO word_index(word_id,frequency_rank,is_name_only,is_expression,is_kanji_backed,meta_payload) VALUES($id,$rank,$nameOnly,$expression,$kanjiBacked,$meta);";
        indexInsert.Parameters.Add("$id", SqliteType.Integer);
        indexInsert.Parameters.Add("$rank", SqliteType.Integer);
        indexInsert.Parameters.Add("$nameOnly", SqliteType.Integer);
        indexInsert.Parameters.Add("$expression", SqliteType.Integer);
        indexInsert.Parameters.Add("$kanjiBacked", SqliteType.Integer);
        indexInsert.Parameters.Add("$meta", SqliteType.Text);
        indexInsert.Prepare();

        long lookupOrdinal = 0;
        var usedIds = new HashSet<int>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 500_000_000,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = true
        };
        using (var reader = XmlReader.Create(jmDictPath, settings))
        {
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    _offlineEntityCodeByDescription = ParseOfflineEntityCodeByDescription(reader.Value);
                    Console.Error.WriteLine($"[JitenOffline JMdict] Recovered {_offlineEntityCodeByDescription.Count:N0} DTD entity descriptions for tag normalization.");
                    reader.Read();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "entry")
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var record = BuildOfflineEntry(element);
                    if (record == null) continue;
                    usedIds.Add(record.Id);
                    StoreOfflineRecord(record, isName: false, wordInsert, lookupInsert, indexInsert, ref lookupOrdinal);
                    entryCount++;
                }
                else reader.Read();
            }
        }
        if (entryCount == 0) throw new InvalidDataException("The selected file contained no JMdict <entry> records.");

        if (jmnedictPath == null)
        {
            Console.Error.WriteLine("[JitenOffline SQLite] JMnedict XML not found; proper-name lookup is disabled. Place JMnedict.xml beside JMdict_e.xml or set JITEN_JMNEDICT_PATH.");
        }
        else
        {
            int syntheticNameId = int.MaxValue;
            using var nameReader = XmlReader.Create(jmnedictPath, settings);
            while (!nameReader.EOF)
            {
                if (nameReader.NodeType == XmlNodeType.Element && nameReader.LocalName == "entry")
                {
                    var element = (XElement)XNode.ReadFrom(nameReader);
                    if (!int.TryParse(Value(element, "ent_seq"), out int sourceId)) continue;
                    int assignedId = sourceId;
                    if (usedIds.Contains(assignedId))
                    {
                        while (usedIds.Contains(syntheticNameId)) syntheticNameId--;
                        assignedId = syntheticNameId--;
                    }
                    var record = BuildOfflineNameEntry(element, assignedId);
                    if (record == null) continue;
                    usedIds.Add(assignedId);
                    StoreOfflineRecord(record, isName: true, wordInsert, lookupInsert, indexInsert, ref lookupOrdinal);
                    jmnedictEntryCount++;
                }
                else nameReader.Read();
            }
        }

        if (jmnedictPath != null && jmnedictEntryCount == 0)
            Console.Error.WriteLine("[JitenOffline SQLite] Warning: JMnedict file was present but no usable name entries were loaded.");

        StoreOfflineCacheInfo(connection, transaction, jmDictHash, jmnedictHash);
        transaction.Commit();
    }

    private static void StoreOfflineRecord(OfflineEntry record, bool isName,
        SqliteCommand wordInsert, SqliteCommand lookupInsert, SqliteCommand indexInsert, ref long lookupOrdinal)
    {
        var word = record.Word;
        var stored = new OfflineStoredWord(
            record.PosTags,
            word.Priorities?.ToArray() ?? record.Priorities,
            record.IsUsuallyKana,
            record.IsFullyArchaic,
            isName,
            word.Forms.Select(f => new OfflineStoredForm(
                f.Text, Convert.ToInt32(f.ReadingIndex, System.Globalization.CultureInfo.InvariantCulture), f.FormType.ToString(),
                f.Priorities?.ToArray() ?? Array.Empty<string>(), f.IsActiveInLatestSource, f.IsObsolete, f.IsSearchOnly)).ToArray());

        wordInsert.Parameters["$id"].Value = record.Id;
        wordInsert.Parameters["$isName"].Value = isName ? 1 : 0;
        wordInsert.Parameters["$payload"].Value = JsonSerializer.Serialize(stored);
        wordInsert.ExecuteNonQuery();

        bool isKanjiBacked = false;
        foreach (var form in word.Forms.Where(f => f.IsActiveInLatestSource && !f.IsObsolete && !f.IsSearchOnly))
        {
            StoreOfflineLookupForm(form.Text, record.Id, lookupInsert, ref lookupOrdinal);

            // The previous offline XML loader added kana-fold aliases for JMdict
            // entries. Persist them too, or cache reuse changes lookup behavior.
            // JMnedict name entries intentionally retain their original exact-form
            // behavior, matching the earlier JMnedict integration.
            if (!isName && IsKanaOnlyLookupKey(form.Text))
            {
                var hiragana = KanaConverter.ToHiragana(form.Text, convertLongVowelMark: false);
                if (!string.Equals(hiragana, form.Text, StringComparison.Ordinal))
                    StoreOfflineLookupForm(hiragana, record.Id, lookupInsert, ref lookupOrdinal);

                var katakana = OfflineToKatakana(hiragana);
                if (!string.Equals(katakana, form.Text, StringComparison.Ordinal))
                    StoreOfflineLookupForm(katakana, record.Id, lookupInsert, ref lookupOrdinal);
            }

            if (ContainsKanji(form.Text)) isKanjiBacked = true;
        }

        var cachedPos = word.CachedPOS;
        bool isNameOnly = cachedPos.Any() && cachedPos.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown);

        int rank;
        if (isName) rank = int.MaxValue - 1;
        else
        {
            var nf = record.Priorities.Select(ParseNfPriority).Where(r => r > 0).DefaultIfEmpty(int.MaxValue).Min();
            rank = nf == int.MaxValue ? int.MaxValue - 1 : nf * 1000 + (record.Id % 997);
        }
        var metaPayload = BuildOfflineWordMetaPayload(record, isName);

        indexInsert.Parameters["$id"].Value = record.Id;
        indexInsert.Parameters["$rank"].Value = rank;
        indexInsert.Parameters["$nameOnly"].Value = isNameOnly ? 1 : 0;
        indexInsert.Parameters["$expression"].Value = !isName && record.PosTags.Contains("exp", StringComparer.Ordinal) ? 1 : 0;
        indexInsert.Parameters["$kanjiBacked"].Value = isKanjiBacked ? 1 : 0;
        indexInsert.Parameters["$meta"].Value = JsonSerializer.Serialize(metaPayload);
        indexInsert.ExecuteNonQuery();
    }

    private static void StoreOfflineLookupForm(string form, int wordId, SqliteCommand lookupInsert, ref long lookupOrdinal)
    {
        if (string.IsNullOrWhiteSpace(form)) return;
        lookupInsert.Parameters["$form"].Value = form;
        lookupInsert.Parameters["$id"].Value = wordId;
        lookupInsert.Parameters["$ordinal"].Value = lookupOrdinal++;
        lookupInsert.ExecuteNonQuery();
    }

    private static OfflineWordMetaPayload BuildOfflineWordMetaPayload(OfflineEntry record, bool isName)
    {
        var pos = record.PosTags.Select(p => p.ToPartOfSpeech()).ToArray();
        bool hasUk = record.IsUsuallyKana;
        bool hasName = pos.Contains(PartOfSpeech.Name);
        bool hasTrueName = hasName && !record.PosTags.Contains("unclass", StringComparer.Ordinal);
        var scoringPriorities = record.Word.Priorities?.ToArray() ?? record.Priorities;
        int score = 0;
        foreach (var p in scoringPriorities)
        {
            switch (p)
            {
                case "jiten": score += 100; break;
                case "ichi1": score += 20; break;
                case "ichi2": score += 10; break;
                case "news1": score += 15; break;
                case "news2": score += 10; break;
                case "gai1": score += 15; break;
                case "gai2": score += 10; break;
                default:
                    if (p.StartsWith("nf", StringComparison.Ordinal) && int.TryParse(p.AsSpan(2), out int n))
                        score += Math.Max(0, 5 - (int)Math.Round(n / 10f));
                    break;
            }
        }
        if (score == 0)
        {
            if (scoringPriorities.Contains("spec1")) score += 15;
            if (scoringPriorities.Contains("spec2")) score += 5;
        }
        if (hasName) score -= 50;
        int delta = hasUk ? 10 : 0;
        if (record.IsFullyArchaic && !HasFrequencyMarker(scoringPriorities)) score -= 350;
        var origin = isName ? ResolveJmnedictOrigin() : ResolveJmDictOrigin();
        return new OfflineWordMetaPayload(record.PosTags, (short)Math.Clamp(score + delta, short.MinValue, short.MaxValue),
            (short)Math.Clamp(score - delta, short.MinValue, short.MaxValue), origin.ToString(), hasTrueName);
    }

    private static Data.JmDictWordMeta MaterializeOfflineWordMeta(OfflineWordMetaPayload payload)
    {
        var pos = payload.PosTags.Select(p => p.ToPartOfSpeech()).ToArray();
        if (!Enum.TryParse<WordOrigin>(payload.Origin, ignoreCase: true, out var origin)) origin = default;
        return new Data.JmDictWordMeta(pos, payload.KanaScore, payload.KanjiScore, origin, payload.HasTrueName);
    }

    private static JmDictWord MaterializeOfflineWord(int id, OfflineStoredWord payload)
    {
        var forms = new List<JmDictWordForm>(payload.Forms.Length);
        foreach (var saved in payload.Forms)
        {
            var form = NewModel<JmDictWordForm>();
            SetModelProperty(form, "Text", saved.Text);
            SetModelProperty(form, "ReadingIndex", saved.ReadingIndex);
            SetModelProperty(form, "FormType", saved.FormType);
            SetModelProperty(form, "Priorities", saved.Priorities.ToList());
            SetModelProperty(form, "IsActiveInLatestSource", saved.IsActiveInLatestSource);
            SetModelProperty(form, "IsObsolete", saved.IsObsolete);
            SetModelProperty(form, "IsSearchOnly", saved.IsSearchOnly);
            forms.Add(form);
        }
        var word = NewModel<JmDictWord>();
        SetModelProperty(word, "WordId", id);
        SetModelProperty(word, "PartsOfSpeech", payload.PosTags);
        SetModelProperty(word, "Priorities", payload.Priorities.ToList());
        SetModelProperty(word, "Forms", forms);
        SetModelProperty(word, "Origin", payload.IsNameEntry ? ResolveJmnedictOrigin() : ResolveJmDictOrigin());
        SetModelProperty(word, "IsFullyArchaic", payload.IsFullyArchaic);
        _ = word.CachedPOS;
        _ = word.CachedPOSMask;
        _ = word.IsSuruVerb;
        return word;
    }

    // GSM_JITEN_OFFLINE_SQLITE_INDEX_V1
    private sealed class OfflineSqliteDictionaryIndex : ILookupIndex, IWordMetaIndex, IReadOnlyDictionary<int, int>
    {
        private readonly string _connectionString;
        private readonly BoundedLruCache<string, List<int>> _lookupCache;
        private readonly BoundedLruCache<int, OfflineIndexRow> _metadataCache;
        private int? _lookupKeyCount;

        // OFFLINE_FREQUENCY_RANK_COMPAT_V1: this sentinel is storage-only; no-frequency entries are absent
        // from Jiten's IReadOnlyDictionary frequency-rank view.
        private const int UnknownOfflineFrequencyRank = int.MaxValue - 1;

        private readonly record struct OfflineIndexRow(
            int FrequencyRank, bool IsNameOnly, bool IsExpression, bool IsKanjiBacked,
            Data.JmDictWordMeta Meta);

        public OfflineSqliteDictionaryIndex(string databasePath, int lookupCacheEntries, int metadataCacheEntries)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 10
            }.ToString();
            _lookupCache = new BoundedLruCache<string, List<int>>(lookupCacheEntries, StringComparer.Ordinal);
            _metadataCache = new BoundedLruCache<int, OfflineIndexRow>(metadataCacheEntries);
        }

        public int LookupKeyCount
        {
            get
            {
                if (_lookupKeyCount.HasValue) return _lookupKeyCount.Value;
                using var connection = OpenReadConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(DISTINCT form) FROM lookup_forms;";
                _lookupKeyCount = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                return _lookupKeyCount.Value;
            }
        }

        public bool TryGetValue(string key, out List<int>? ids)
        {
            PrefetchLookupKeys([key]);
            if (_lookupCache.TryGetValue(key, out ids)) return ids.Count > 0;
            ids = null;
            return false;
        }

        public bool TryGetValue(ReadOnlySpan<char> key, out List<int>? ids) => TryGetValue(key.ToString(), out ids);
        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public void PrefetchLookupKeys(IEnumerable<string> keys)
        {
            var distinct = keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal)
                .Where(k => !_lookupCache.TryGetValue(k, out _)).ToArray();
            for (int offset = 0; offset < distinct.Length; offset += 300)
            {
                var batch = distinct.Skip(offset).Take(300).ToArray();
                if (batch.Length == 0) continue;
                var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                using var connection = OpenReadConnection();
                using var command = connection.CreateCommand();
                var placeholders = new string[batch.Length];
                for (int i = 0; i < batch.Length; i++)
                {
                    placeholders[i] = "$k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(placeholders[i], batch[i]);
                }
                command.CommandText = $"SELECT form,word_id FROM lookup_forms WHERE form IN ({string.Join(",", placeholders)}) ORDER BY form,ordinal;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var form = reader.GetString(0);
                        if (!found.TryGetValue(form, out var list)) found[form] = list = new List<int>();
                        list.Add(reader.GetInt32(1));
                    }
                }
                foreach (var key in batch)
                    _lookupCache.Set(key, found.TryGetValue(key, out var list) ? list : new List<int>());
            }
        }

        public IEnumerable<string> EnumerateKeys()
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT form FROM lookup_forms ORDER BY form;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return reader.GetString(0);
        }

        public bool TryGetValue(int wordId, out Data.JmDictWordMeta meta)
        {
            if (TryGetIndexRow(wordId, out var row)) { meta = row.Meta; return true; }
            meta = default;
            return false;
        }

        public bool IsNameOnly(int wordId) => TryGetIndexRow(wordId, out var row) && row.IsNameOnly;
        public bool IsExpression(int wordId) => TryGetIndexRow(wordId, out var row) && row.IsExpression;
        public bool IsKanjiBacked(int wordId) => TryGetIndexRow(wordId, out var row) && row.IsKanjiBacked;
        public void PrefetchWordMetadata(IEnumerable<int> wordIds)
        {
            var ids = wordIds.Distinct().Where(id => !_metadataCache.TryGetValue(id, out _)).ToArray();
            for (int offset = 0; offset < ids.Length; offset += 300)
            {
                var batch = ids.Skip(offset).Take(300).ToArray();
                if (batch.Length == 0) continue;
                using var connection = OpenReadConnection();
                using var command = connection.CreateCommand();
                var placeholders = new string[batch.Length];
                for (int i = 0; i < batch.Length; i++)
                {
                    placeholders[i] = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(placeholders[i], batch[i]);
                }
                command.CommandText = $"SELECT word_id,frequency_rank,is_name_only,is_expression,is_kanji_backed,meta_payload FROM word_index WHERE word_id IN ({string.Join(",", placeholders)});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    CacheIndexRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0,
                        reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetString(5));
            }
        }

        private bool TryGetIndexRow(int wordId, out OfflineIndexRow row)
        {
            if (_metadataCache.TryGetValue(wordId, out row)) return true;
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT frequency_rank,is_name_only,is_expression,is_kanji_backed,meta_payload FROM word_index WHERE word_id=$id;";
            command.Parameters.AddWithValue("$id", wordId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { row = default; return false; }
            row = CacheIndexRow(wordId, reader.GetInt32(0), reader.GetInt32(1) != 0,
                reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, reader.GetString(4));
            return true;
        }

        private OfflineIndexRow CacheIndexRow(int id, int rank, bool nameOnly, bool expression, bool kanjiBacked, string payloadText)
        {
            var payload = JsonSerializer.Deserialize<OfflineWordMetaPayload>(payloadText)
                ?? throw new InvalidDataException($"Invalid offline metadata payload for word ID {id}.");
            var row = new OfflineIndexRow(rank, nameOnly, expression, kanjiBacked, Parser.MaterializeOfflineWordMeta(payload));
            _metadataCache.Set(id, row);
            return row;
        }

        private SqliteConnection OpenReadConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        // Frequency-rank view. Entries without a real rank are not dictionary keys.
        private bool TryGetKnownFrequencyRank(int wordId, out int rank)
        {
            if (TryGetIndexRow(wordId, out var row) && row.FrequencyRank != UnknownOfflineFrequencyRank)
            {
                rank = row.FrequencyRank;
                return true;
            }

            rank = default;
            return false;
        }

        int IReadOnlyCollection<KeyValuePair<int, int>>.Count => GetWordIndexCount();
        IEnumerable<int> IReadOnlyDictionary<int, int>.Keys => EnumerateWordIds();
        IEnumerable<int> IReadOnlyDictionary<int, int>.Values => EnumerateWordIds().Select(id => ((IReadOnlyDictionary<int, int>)this)[id]);
        int IReadOnlyDictionary<int, int>.this[int key] => TryGetKnownFrequencyRank(key, out var rank) ? rank : throw new KeyNotFoundException();
        bool IReadOnlyDictionary<int, int>.ContainsKey(int key) => TryGetKnownFrequencyRank(key, out _);
        bool IReadOnlyDictionary<int, int>.TryGetValue(int key, out int value) => TryGetKnownFrequencyRank(key, out value);
        IEnumerator<KeyValuePair<int, int>> IEnumerable<KeyValuePair<int, int>>.GetEnumerator()
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT word_id,frequency_rank FROM word_index WHERE frequency_rank<>$unknown ORDER BY word_id;";
            command.Parameters.AddWithValue("$unknown", UnknownOfflineFrequencyRank);
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new KeyValuePair<int, int>(reader.GetInt32(0), reader.GetInt32(1));
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            ((IEnumerable<KeyValuePair<int, int>>)this).GetEnumerator();
        private IEnumerable<int> EnumerateWordIds()
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT word_id FROM word_index WHERE frequency_rank<>$unknown ORDER BY word_id;";
            command.Parameters.AddWithValue("$unknown", UnknownOfflineFrequencyRank);
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return reader.GetInt32(0);
        }
        private int GetWordIndexCount()
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM word_index WHERE frequency_rank<>$unknown;";
            command.Parameters.AddWithValue("$unknown", UnknownOfflineFrequencyRank);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _lru = new();
        private readonly object _gate = new();
        public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
        {
            _capacity = Math.Max(128, capacity);
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(comparer);
        }
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node); _lru.AddFirst(node); value = node.Value.Value; return true;
                }
            }
            value = default!; return false;
        }
        public void Set(TKey key, TValue value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = (key, value); _lru.Remove(existing); _lru.AddFirst(existing); return;
                }
                var node = _lru.AddFirst((key, value)); _map[key] = node;
                while (_map.Count > _capacity && _lru.Last != null)
                {
                    var last = _lru.Last; _lru.RemoveLast(); _map.Remove(last.Value.Key);
                }
            }
        }
    }


    private sealed class OfflineJmDictCache : IJmDictCache
    {
        public const int MaxCachedWords = 6000;
        private readonly SqliteConnection _connection;
        private readonly Dictionary<int, JmDictWord> _words = new();
        private readonly Queue<int> _insertionOrder = new();
        private readonly object _gate = new();

        public OfflineJmDictCache(string databasePath)
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString());
            _connection.Open();
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA cache_size=-4096;";
            pragma.ExecuteNonQuery();
        }

        public Task<JmDictWord?> GetWordAsync(int wordId)
        {
            var words = GetWordsAsync(new[] { wordId }).GetAwaiter().GetResult();
            return Task.FromResult(words.TryGetValue(wordId, out var word) ? word : null);
        }

        public Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds)
        {
            lock (_gate)
            {
                var ids = wordIds.Distinct().ToArray();
                var result = new Dictionary<int, JmDictWord>(ids.Length);
                var missing = new List<int>();
                foreach (var id in ids)
                {
                    if (_words.TryGetValue(id, out var cached)) result[id] = cached;
                    else missing.Add(id);
                }

                for (int offset = 0; offset < missing.Count; offset += 400)
                {
                    var batch = missing.Skip(offset).Take(400).ToArray();
                    if (batch.Length == 0) continue;
                    using var command = _connection.CreateCommand();
                    var placeholders = new string[batch.Length];
                    for (int i = 0; i < batch.Length; i++)
                    {
                        placeholders[i] = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        command.Parameters.AddWithValue(placeholders[i], batch[i]);
                    }
                    command.CommandText = $"SELECT word_id,payload FROM word_records WHERE word_id IN ({string.Join(",", placeholders)});";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        int id = reader.GetInt32(0);
                        var payload = JsonSerializer.Deserialize<OfflineStoredWord>(reader.GetString(1));
                        if (payload == null) continue;
                        var word = MaterializeOfflineWord(id, payload);
                        AddToCache(id, word);
                        result[id] = word;
                    }
                }
                return Task.FromResult(result);
            }
        }

        private void AddToCache(int id, JmDictWord word)
        {
            if (!_words.ContainsKey(id)) _insertionOrder.Enqueue(id);
            _words[id] = word;
            while (_words.Count > MaxCachedWords && _insertionOrder.Count > 0)
                _words.Remove(_insertionOrder.Dequeue());
        }

        public Task<bool> SetWordAsync(int wordId, JmDictWord word)
        {
            lock (_gate) { PriorityOverrides.Apply(word); AddToCache(wordId, word); }
            return Task.FromResult(true);
        }

        public Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words)
        {
            lock (_gate) foreach (var pair in words) { PriorityOverrides.Apply(pair.Value); AddToCache(pair.Key, pair.Value); }
            return Task.FromResult(true);
        }

        public Task<bool> IsCacheInitializedAsync() => Task.FromResult(true);
        public Task SetCacheInitializedAsync() => Task.CompletedTask;
    }


    // Optional JmdictFurigana parsing omitted: this checkout has no compatible PartialKanaSpellings builder.

    private static OfflineEntry? BuildOfflineEntry(XElement entry)
    {
        if (!int.TryParse(Value(entry, "ent_seq"), out int id)) return null;
        var kElements = Children(entry, "k_ele").Select(k => new OfflineKanji(
            Value(k, "keb"), Values(k, "ke_pri").Distinct().ToArray(), Values(k, "ke_inf").Distinct().ToArray()))
            .Where(k => k.Text.Length > 0).ToList();
        var rElements = Children(entry, "r_ele").Select(r => new OfflineReading(
            Value(r, "reb"), Children(r, "re_nokanji").Any(), Values(r, "re_restr").ToArray(),
            Values(r, "re_pri").Distinct().ToArray(), Values(r, "re_inf").Distinct().ToArray()))
            .Where(r => r.Text.Length > 0).ToList();
        var senses = Children(entry, "sense").ToList();
        var posTags = senses.SelectMany(s => Values(s, "pos")).Distinct(StringComparer.Ordinal).ToList();
        bool isUk = senses.SelectMany(s => Values(s, "misc")).Contains("uk", StringComparer.Ordinal);
        bool isFullyArchaic = senses.Count > 0 && senses.All(s => Values(s, "misc").Contains("arch", StringComparer.Ordinal));
        if (isUk) posTags.Add("uk");
        if (posTags.Count == 0) posTags.Add("unclass");
        var priorities = kElements.SelectMany(k => k.Priorities).Concat(rElements.SelectMany(r => r.Priorities))
            .Distinct(StringComparer.Ordinal).ToArray();

        var forms = new List<JmDictWordForm>();
        for (int ri = 0; ri < rElements.Count; ri++)
        {
            var reading = rElements[ri];
            var kana = NewModel<JmDictWordForm>();
            SetModelProperty(kana, "Text", reading.Text);
            SetModelProperty(kana, "ReadingIndex", ri);
            SetModelProperty(kana, "FormType", "KanaForm");
            SetModelProperty(kana, "Priorities", reading.Priorities.ToList());
            SetModelProperty(kana, "IsActiveInLatestSource", true);
            SetModelProperty(kana, "IsObsolete", reading.Info.Any(IsObsoleteInfo));
            SetModelProperty(kana, "IsSearchOnly", reading.Info.Any(IsSearchOnlyInfo));
            forms.Add(kana);

            if (reading.NoKanji) continue;
            foreach (var kanji in kElements)
            {
                if (reading.Restrictions.Length > 0 && !reading.Restrictions.Contains(kanji.Text, StringComparer.Ordinal)) continue;
                var written = NewModel<JmDictWordForm>();
                SetModelProperty(written, "Text", kanji.Text);
                SetModelProperty(written, "ReadingIndex", ri);
                SetModelProperty(written, "FormType", "KanjiForm");
                SetModelProperty(written, "Priorities", kanji.Priorities.ToList());
                SetModelProperty(written, "IsActiveInLatestSource", true);
                SetModelProperty(written, "IsObsolete", kanji.Info.Any(IsObsoleteInfo));
                SetModelProperty(written, "IsSearchOnly", kanji.Info.Any(IsSearchOnlyInfo));
                forms.Add(written);
            }
        }

        var word = NewModel<JmDictWord>();
        SetModelProperty(word, "WordId", id);
        SetModelProperty(word, "PartsOfSpeech", posTags.ToArray());
        SetModelProperty(word, "Priorities", priorities.ToList());
        SetModelProperty(word, "Forms", forms);
        SetModelProperty(word, "Origin", ResolveJmDictOrigin());
        SetModelProperty(word, "IsFullyArchaic", isFullyArchaic);
        // This offline loader currently reconstructs forms/POS/priorities, but not per-sense
        // definition records and their stagk/stagr restrictions. Word-level matching still runs;
        // ambiguous readings can differ from the DB-backed Jiten parser until those records are mapped.
        PriorityOverrides.Apply(word);
        _ = word.CachedPOS;
        _ = word.CachedPOSMask;
        _ = word.IsSuruVerb;

        return new OfflineEntry
        {
            Id = id, Kanji = kElements, Readings = rElements, PosTags = posTags.ToArray(),
            Priorities = priorities, IsUsuallyKana = isUk, IsFullyArchaic = isFullyArchaic, Word = word
        };
    }

    // GSM_JITEN_OFFLINE_JMNEDICT_V1
    private static string? ResolveOfflineJmnedictPath(string jmDictFullPath)
    {
        var configured = Environment.GetEnvironmentVariable("JITEN_JMNEDICT_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredFullPath = Path.GetFullPath(configured);
            if (!File.Exists(configuredFullPath))
                throw new FileNotFoundException("JITEN_JMNEDICT_PATH points to a missing JMnedict XML file.", configuredFullPath);
            return configuredFullPath;
        }

        var directory = Path.GetDirectoryName(jmDictFullPath)!;
        foreach (var fileName in new[] { "JMnedict.xml", "JMnedict_e.xml", "jmnedict.xml", "jmnedict_e.xml" })
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static OfflineEntry? BuildOfflineNameEntry(XElement entry, int assignedId)
    {
        var kanji = Children(entry, "k_ele").Select(k => new OfflineKanji(
            Value(k, "keb"), Values(k, "ke_pri").Distinct().ToArray(), Values(k, "ke_inf").Distinct().ToArray()))
            .Where(k => k.Text.Length > 0).ToList();
        var readings = Children(entry, "r_ele").Select(r => new OfflineReading(
            Value(r, "reb"), Children(r, "re_nokanji").Any(), Values(r, "re_restr").ToArray(),
            Values(r, "re_pri").Distinct().ToArray(), Values(r, "re_inf").Distinct().ToArray()))
            .Where(r => r.Text.Length > 0).ToList();
        if (readings.Count == 0) return null;

        var forms = new List<JmDictWordForm>();
        for (int readingIndex = 0; readingIndex < readings.Count; readingIndex++)
        {
            var reading = readings[readingIndex];
            var kana = NewModel<JmDictWordForm>();
            SetModelProperty(kana, "Text", reading.Text);
            SetModelProperty(kana, "ReadingIndex", readingIndex);
            SetModelProperty(kana, "FormType", "KanaForm");
            SetModelProperty(kana, "Priorities", reading.Priorities.ToList());
            SetModelProperty(kana, "IsActiveInLatestSource", true);
            SetModelProperty(kana, "IsObsolete", reading.Info.Any(IsObsoleteInfo));
            SetModelProperty(kana, "IsSearchOnly", reading.Info.Any(IsSearchOnlyInfo));
            forms.Add(kana);

            if (reading.NoKanji) continue;
            foreach (var written in kanji)
            {
                if (reading.Restrictions.Length > 0
                    && !reading.Restrictions.Contains(written.Text, StringComparer.Ordinal)) continue;
                var kanjiForm = NewModel<JmDictWordForm>();
                SetModelProperty(kanjiForm, "Text", written.Text);
                SetModelProperty(kanjiForm, "ReadingIndex", readingIndex);
                SetModelProperty(kanjiForm, "FormType", "KanjiForm");
                SetModelProperty(kanjiForm, "Priorities", written.Priorities.ToList());
                SetModelProperty(kanjiForm, "IsActiveInLatestSource", true);
                SetModelProperty(kanjiForm, "IsObsolete", written.Info.Any(IsObsoleteInfo));
                SetModelProperty(kanjiForm, "IsSearchOnly", written.Info.Any(IsSearchOnlyInfo));
                forms.Add(kanjiForm);
            }
        }

        var word = NewModel<JmDictWord>();
        SetModelProperty(word, "WordId", assignedId);
        // JMnedict entries are proper-name entries. The parser's name merge logic uses this POS.
        SetModelProperty(word, "PartsOfSpeech", new[] { "name" });
        SetModelProperty(word, "Priorities", new List<string>());
        SetModelProperty(word, "Forms", forms);
        SetModelProperty(word, "Origin", ResolveJmnedictOrigin());
        SetModelProperty(word, "IsFullyArchaic", false);
        PriorityOverrides.Apply(word);
        _ = word.CachedPOS;
        _ = word.CachedPOSMask;
        _ = word.IsSuruVerb;

        return new OfflineEntry
        {
            Id = assignedId,
            Kanji = kanji,
            Readings = readings,
            PosTags = new[] { "name" },
            Priorities = Array.Empty<string>(),
            IsUsuallyKana = false,
            IsFullyArchaic = false,
            Word = word,
            IsNameEntry = true
        };
    }

    private static WordOrigin ResolveJmnedictOrigin()
    {
        foreach (var name in new[] { "JMnedict", "Jmnedict", "JMNedict", "Name" })
            if (Enum.TryParse<WordOrigin>(name, ignoreCase: true, out var origin)) return origin;
        return ResolveJmDictOrigin();
    }

    private static Data.JmDictWordMeta BuildOfflineWordMeta(OfflineEntry record)
    {
        var pos = record.PosTags.Select(p => p.ToPartOfSpeech()).ToArray();
        bool hasUk = record.IsUsuallyKana;
        bool hasName = pos.Contains(PartOfSpeech.Name);
        bool hasTrueName = hasName && !record.PosTags.Contains("unclass", StringComparer.Ordinal);
        int score = 0;
        var scoringPriorities = record.Word.Priorities?.ToArray() ?? record.Priorities;
        foreach (var p in scoringPriorities)
        {
            switch (p)
            {
                case "jiten": score += 100; break;
                case "ichi1": score += 20; break;
                case "ichi2": score += 10; break;
                case "news1": score += 15; break;
                case "news2": score += 10; break;
                case "gai1": score += 15; break;
                case "gai2": score += 10; break;
                default:
                    if (p.StartsWith("nf", StringComparison.Ordinal) && int.TryParse(p.AsSpan(2), out int n))
                        score += Math.Max(0, 5 - (int)Math.Round(n / 10f));
                    break;
            }
        }
        if (score == 0)
        {
            if (scoringPriorities.Contains("spec1")) score += 15;
            if (scoringPriorities.Contains("spec2")) score += 5;
        }
        if (hasName) score -= 50;
        int delta = hasUk ? 10 : 0;
        if (record.IsFullyArchaic && !HasFrequencyMarker(scoringPriorities)) score -= 350;
        return new Data.JmDictWordMeta(pos, (short)Math.Clamp(score + delta, short.MinValue, short.MaxValue),
            (short)Math.Clamp(score - delta, short.MinValue, short.MaxValue), record.IsNameEntry ? ResolveJmnedictOrigin() : ResolveJmDictOrigin(), hasTrueName);
    }

    private static WordOrigin ResolveJmDictOrigin()
    {
        foreach (var name in new[] { "JMdict", "JmDict", "JMDict", "Dictionary" })
            if (Enum.TryParse<WordOrigin>(name, ignoreCase: true, out var origin)) return origin;
        return default;
    }

    private static bool HasFrequencyMarker(IEnumerable<string> priorities) => priorities.Any(p =>
        p is "ichi1" or "ichi2" or "news1" or "news2" or "gai1" or "gai2" or "spec1" or "spec2"
        || p.StartsWith("nf", StringComparison.Ordinal));

    private static int ParseNfPriority(string value) =>
        value.StartsWith("nf", StringComparison.Ordinal) && int.TryParse(value.AsSpan(2), out int rank) ? rank : 0;

    // GSM_JITEN_OFFLINE_KANA_LOOKUP_FOLD_V1
    // Recreate Jiten's hiragana/katakana equivalence for kana-only lookup forms.
    private static void AddKanaFoldAliases(Dictionary<string, List<int>> lookups, string key, int id)
    {
        if (string.IsNullOrEmpty(key) || !IsKanaOnlyLookupKey(key))
            return;

        var hiragana = KanaConverter.ToHiragana(key, convertLongVowelMark: false);
        if (!string.Equals(hiragana, key, StringComparison.Ordinal))
            AddLookup(lookups, hiragana, id);

        var katakana = OfflineToKatakana(hiragana);
        if (!string.Equals(katakana, key, StringComparison.Ordinal))
            AddLookup(lookups, katakana, id);
    }

    private static bool IsKanaOnlyLookupKey(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
        {
            bool hira = (c >= '\u3041' && c <= '\u3096')
                || c == '\u309D' || c == '\u309E' || c == '\u309F';
            bool kata = c >= '\u30A1' && c <= '\u30FF';
            if (!hira && !kata)
                return false;
        }
        return true;
    }

    private static bool IsHiraganaLookupChar(char c) =>
        (c >= '\u3041' && c <= '\u3096')
        || c == '\u309D' || c == '\u309E' || c == '\u309F';

    private static string OfflineToKatakana(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (IsHiraganaLookupChar(chars[i]))
                chars[i] = (char)(chars[i] + 0x60);
        }
        return new string(chars);
    }

    private static void AddLookup(Dictionary<string, List<int>> lookups, string key, int id)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!lookups.TryGetValue(key, out var ids)) lookups[key] = ids = new List<int>();
        if (!ids.Contains(id)) ids.Add(id);
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName == name);

    private static string Value(XElement parent, string name) =>
        Children(parent, name).Select(e => e.Value.Trim()).FirstOrDefault() ?? string.Empty;

    private static IEnumerable<string> Values(XElement parent, string name) =>
        Children(parent, name).Select(e => NormalizeOfflineJmDictTagValue(name, e.Value.Trim())).Where(s => s.Length > 0);

    private static bool IsObsoleteInfo(string value) =>
        value.Contains("out-dated", StringComparison.OrdinalIgnoreCase)
        || value.Contains("obsolete", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ok", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchOnlyInfo(string value) =>
        value.Contains("search-only", StringComparison.OrdinalIgnoreCase)
        || value.Equals("sk", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsKanji(string text) => text.Any(c =>
        c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF');

    private static T NewModel<T>() where T : class
    {
        var value = Activator.CreateInstance(typeof(T), nonPublic: true);
        if (value is T typed) return typed;
        throw new InvalidOperationException($"Could not create {typeof(T).FullName}. Expected a parameterless model constructor.");
    }

    private static void SetModelProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
            throw new InvalidOperationException($"Jiten model {target.GetType().FullName} has no expected property '{propertyName}'. Check that this patch matches your Jiten source version.");
        if (value == null) return;

        var setter = property.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(target, [ConvertForProperty(value, property.PropertyType)]);
            return;
        }

        // Accommodate EF-style get-only collections if this model version uses them.
        if (property.GetValue(target) is System.Collections.IList current && value is System.Collections.IEnumerable source)
        {
            foreach (var item in source) current.Add(item);
            return;
        }
        throw new InvalidOperationException($"Jiten model property '{propertyName}' on {target.GetType().FullName} is not writable.");
    }

    private static object ConvertForProperty(object value, Type propertyType)
    {
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum)
        {
            if (value is string name)
            {
                foreach (var candidate in ExpandEnumNameCandidates(name))
                {
                    try { return Enum.Parse(target, candidate, ignoreCase: true); }
                    catch (ArgumentException) { }
                }
            }
            return Enum.ToObject(target, 0);
        }
        if (target.IsArray && value is System.Collections.IEnumerable enumerable)
        {
            var elementType = target.GetElementType()!;
            var items = enumerable.Cast<object?>().ToList();
            var array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(items[i] == null ? null : ConvertForProperty(items[i]!, elementType), i);
            return array;
        }
        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = target.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(target)!;
            if (value is System.Collections.IEnumerable items)
                foreach (var item in items) list.Add(item == null ? null : ConvertForProperty(item, elementType));
            return list;
        }
        if (target.IsGenericType && (target.GetGenericTypeDefinition() == typeof(IList<>)
            || target.GetGenericTypeDefinition() == typeof(ICollection<>)
            || target.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)
            || target.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            || target.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            var elementType = target.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            if (value is System.Collections.IEnumerable items)
                foreach (var item in items) list.Add(item == null ? null : ConvertForProperty(item, elementType));
            return list;
        }
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture)!;
        if (target.IsAssignableFrom(value.GetType())) return value;
        return value;
    }

    private static IEnumerable<string> ExpandEnumNameCandidates(string name)
    {
        yield return name;
        if (name.Equals("JMdict", StringComparison.OrdinalIgnoreCase))
        {
            yield return "JmDict";
            yield return "JMDict";
            yield return "Dictionary";
        }
    }

    private sealed class OfflineDeckWordCache : IDeckWordCache
    {
        private readonly ConcurrentDictionary<DeckWordCacheKey, DeckWord> _words = new();
        public Task<DeckWord?> GetAsync(DeckWordCacheKey key) => Task.FromResult(_words.TryGetValue(key, out var word) ? word.Clone() : null);
        public Task<Dictionary<DeckWordCacheKey, DeckWord?>> GetManyAsync(IReadOnlyList<DeckWordCacheKey> keys)
        {
            var result = new Dictionary<DeckWordCacheKey, DeckWord?>(keys.Count);
            foreach (var key in keys) result[key] = _words.TryGetValue(key, out var word) ? word.Clone() : null;
            return Task.FromResult(result);
        }
        public Task SetAsync(DeckWordCacheKey key, DeckWord word, bool fireAndForget = false) { _words[key] = word.Clone(); return Task.CompletedTask; }
        public Task SetManyAsync(IReadOnlyList<(DeckWordCacheKey key, DeckWord word)> entries, bool fireAndForget = false) { foreach (var (key, word) in entries) _words[key] = word.Clone(); return Task.CompletedTask; }
    }
}
