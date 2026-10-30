// GSM_JITEN_OFFLINE_SQLITE_INDEX_V1
using Jiten.Core.Data;
using Jiten.Parser.Data;

namespace Jiten.Parser;

internal interface ILookupIndex
{
    int LookupKeyCount { get; }
    bool TryGetValue(string key, out List<int>? ids);
    bool TryGetValue(ReadOnlySpan<char> key, out List<int>? ids);
    bool ContainsKey(string key);
    IEnumerable<string> EnumerateKeys();
    void PrefetchLookupKeys(IEnumerable<string> keys);
}

internal interface IWordMetaIndex
{
    bool TryGetValue(int wordId, out JmDictWordMeta meta);
    bool IsNameOnly(int wordId);
    bool IsExpression(int wordId);
    bool IsKanjiBacked(int wordId);
    void PrefetchWordMetadata(IEnumerable<int> wordIds);
}

/// <summary>Adapter for the normal online runtime and dictionary-based tests.</summary>
internal sealed class InMemoryParserIndex : ILookupIndex, IWordMetaIndex, IReadOnlyDictionary<int, int>
{
    private readonly Dictionary<string, List<int>> _lookups;
    private readonly Dictionary<string, List<int>>.AlternateLookup<ReadOnlySpan<char>> _alternate;
    private readonly Dictionary<int, int> _frequencyRanks;
    private readonly HashSet<int> _nameOnly;
    private readonly HashSet<int> _expressions;
    private readonly HashSet<int> _kanjiBacked;
    private readonly Dictionary<int, JmDictWordMeta> _metadata;

    public InMemoryParserIndex(
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        HashSet<int> nameOnly,
        HashSet<int> expressions,
        HashSet<int> kanjiBacked,
        Dictionary<int, JmDictWordMeta> metadata)
    {
        _lookups = lookups;
        _alternate = lookups.GetAlternateLookup<ReadOnlySpan<char>>();
        _frequencyRanks = frequencyRanks;
        _nameOnly = nameOnly;
        _expressions = expressions;
        _kanjiBacked = kanjiBacked;
        _metadata = metadata;
    }

    public int LookupKeyCount => _lookups.Count;
    public bool TryGetValue(string key, out List<int>? ids) => _lookups.TryGetValue(key, out ids);
    public bool TryGetValue(ReadOnlySpan<char> key, out List<int>? ids) => _alternate.TryGetValue(key, out ids);
    public bool ContainsKey(string key) => _lookups.ContainsKey(key);
    public IEnumerable<string> EnumerateKeys() => _lookups.Keys;
    public void PrefetchLookupKeys(IEnumerable<string> keys) { }
    public bool TryGetValue(int wordId, out JmDictWordMeta meta) => _metadata.TryGetValue(wordId, out meta);
    public bool IsNameOnly(int wordId) => _nameOnly.Contains(wordId);
    public bool IsExpression(int wordId) => _expressions.Contains(wordId);
    public bool IsKanjiBacked(int wordId) => _kanjiBacked.Contains(wordId);
    public void PrefetchWordMetadata(IEnumerable<int> wordIds) { }

    int IReadOnlyCollection<KeyValuePair<int, int>>.Count => _frequencyRanks.Count;
    IEnumerable<int> IReadOnlyDictionary<int, int>.Keys => _frequencyRanks.Keys;
    IEnumerable<int> IReadOnlyDictionary<int, int>.Values => _frequencyRanks.Values;
    int IReadOnlyDictionary<int, int>.this[int key] => _frequencyRanks[key];
    bool IReadOnlyDictionary<int, int>.ContainsKey(int key) => _frequencyRanks.ContainsKey(key);
    bool IReadOnlyDictionary<int, int>.TryGetValue(int key, out int value) => _frequencyRanks.TryGetValue(key, out value);
    IEnumerator<KeyValuePair<int, int>> IEnumerable<KeyValuePair<int, int>>.GetEnumerator() => _frequencyRanks.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _frequencyRanks.GetEnumerator();
}

/// <summary>Small dictionary adapter used by resegmentation tests that construct lookup maps inline.</summary>
internal sealed class InMemoryLookupIndex : ILookupIndex
{
    private readonly Dictionary<string, List<int>> _lookups;
    private readonly Dictionary<string, List<int>>.AlternateLookup<ReadOnlySpan<char>> _alternate;
    public InMemoryLookupIndex(Dictionary<string, List<int>> lookups)
    {
        _lookups = lookups;
        _alternate = lookups.GetAlternateLookup<ReadOnlySpan<char>>();
    }
    public int LookupKeyCount => _lookups.Count;
    public bool TryGetValue(string key, out List<int>? ids) => _lookups.TryGetValue(key, out ids);
    public bool TryGetValue(ReadOnlySpan<char> key, out List<int>? ids) => _alternate.TryGetValue(key, out ids);
    public bool ContainsKey(string key) => _lookups.ContainsKey(key);
    public IEnumerable<string> EnumerateKeys() => _lookups.Keys;
    public void PrefetchLookupKeys(IEnumerable<string> keys) { }
}

/// <summary>Dictionary adapter for older tests and utility callers.</summary>
internal sealed class InMemoryWordMetaIndex : IWordMetaIndex
{
    private readonly Dictionary<int, JmDictWordMeta> _metadata;
    private readonly HashSet<int> _nameOnly = new();
    private readonly HashSet<int> _expressions = new();
    private readonly HashSet<int> _kanjiBacked = new();
    public InMemoryWordMetaIndex(Dictionary<int, JmDictWordMeta> metadata)
    {
        _metadata = metadata;
        foreach (var (id, meta) in metadata)
        {
            if (meta.Pos.Length > 0 && meta.Pos.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown)) _nameOnly.Add(id);
            if (meta.Pos.Contains(PartOfSpeech.Expression)) _expressions.Add(id);
        }
    }
    public bool TryGetValue(int wordId, out JmDictWordMeta meta) => _metadata.TryGetValue(wordId, out meta);
    public bool IsNameOnly(int wordId) => _nameOnly.Contains(wordId);
    public bool IsExpression(int wordId) => _expressions.Contains(wordId);
    public bool IsKanjiBacked(int wordId) => _kanjiBacked.Contains(wordId);
    public void PrefetchWordMetadata(IEnumerable<int> wordIds) { }
}
