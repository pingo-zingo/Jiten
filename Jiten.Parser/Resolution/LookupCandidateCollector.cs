namespace Jiten.Parser.Resolution;

internal static class LookupCandidateCollector
{
    public static bool HasAnyMatch(Dictionary<string, List<int>> lookups, string text, bool includeLongVowelStripped = false)
        => HasAnyMatch(new InMemoryLookupIndex(lookups), text, includeLongVowelStripped);

    public static List<int> CollectIds(Dictionary<string, List<int>> lookups, string text,
        bool includeKanaNormalized = true, bool includeLongVowelStripped = false, Func<int, bool>? normalizedTierGate = null)
        => CollectIds(new InMemoryLookupIndex(lookups), text, includeKanaNormalized, includeLongVowelStripped, normalizedTierGate);



    /// Checks whether any lookup variant (raw, hiragana, normalized, long-vowel-stripped) has at least one match.
    public static bool HasAnyMatch(
        ILookupIndex lookups,
        string text,
        bool includeLongVowelStripped = false)
    {
        var prefetchHiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        var prefetchKeys = new List<string> { text, prefetchHiragana, KanaNormalizer.Normalize(prefetchHiragana) };
        if (includeLongVowelStripped && text.Contains('ー'))
            prefetchKeys.Add(KanaConverter.ToNormalizedHiragana(text.Replace("ー", "")));
        lookups.PrefetchLookupKeys(prefetchKeys);

        if (lookups.TryGetValue(text, out var ids) && ids is { Count: > 0 })
            return true;

        var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        if (hiragana != text && lookups.TryGetValue(hiragana, out ids) && ids.Count > 0)
            return true;

        var normalized = KanaNormalizer.Normalize(hiragana);
        if (normalized != hiragana && lookups.TryGetValue(normalized, out ids) && ids.Count > 0)
            return true;

        if (includeLongVowelStripped && text.Contains('ー'))
        {
            var stripped = text.Replace("ー", "");
            if (stripped.Length > 0)
            {
                var strippedHira = KanaConverter.ToNormalizedHiragana(stripped);
                if (lookups.TryGetValue(strippedHira, out ids) && ids.Count > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>normalizedTierGate filters normalized-only hits, which invent readings (いえー → いえい must not reach 遺影).</summary>
    public static List<int> CollectIds(
        ILookupIndex lookups,
        string text,
        bool includeKanaNormalized = true,
        bool includeLongVowelStripped = false,
        Func<int, bool>? normalizedTierGate = null)
    {
        var hiraForPrefetch = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        var keysToPrefetch = new List<string> { text, hiraForPrefetch };
        if (includeKanaNormalized)
        {
            var normalizedForPrefetch = KanaNormalizer.Normalize(hiraForPrefetch);
            if (normalizedForPrefetch != hiraForPrefetch) keysToPrefetch.Add(normalizedForPrefetch);
        }
        if (includeLongVowelStripped && text.Contains('ー'))
        {
            var strippedForPrefetch = text.Replace("ー", "");
            if (strippedForPrefetch.Length > 0) keysToPrefetch.Add(KanaConverter.ToHiragana(strippedForPrefetch));
        }
        lookups.PrefetchLookupKeys(keysToPrefetch);

        var seen = new HashSet<int>();
        var ids = new List<int>();

        void AddRange(List<int> source, bool gated = false)
        {
            foreach (var id in source)
            {
                if (gated && normalizedTierGate != null && !seen.Contains(id) && !normalizedTierGate(id))
                    continue;
                if (seen.Add(id)) ids.Add(id);
            }
        }

        if (lookups.TryGetValue(text, out var direct)) AddRange(direct);

        var hiragana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
        if (hiragana != text && lookups.TryGetValue(hiragana, out var hiraIds))
            AddRange(hiraIds);

        if (includeKanaNormalized)
        {
            var normalized = KanaNormalizer.Normalize(hiragana);
            if (normalized != hiragana && lookups.TryGetValue(normalized, out var normIds))
                AddRange(normIds, gated: true);
        }

        if (includeLongVowelStripped && text.Contains('ー'))
        {
            var textStripped = text.Replace("ー", "");
            bool endsInBar = text.EndsWith('ー');
            bool isShort = textStripped.Length <= 2;

            if (!endsInBar || !isShort)
            {
                var strippedHira = KanaConverter.ToHiragana(textStripped);
                if (lookups.TryGetValue(strippedHira, out var stripIds))
                    AddRange(stripIds, gated: true);
            }
        }

        return ids;
    }
}
