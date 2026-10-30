using Jiten.Core.Data;

namespace Jiten.Parser.Data.Redis;

public interface IDeckWordCache
{
    Task<DeckWord?> GetAsync(DeckWordCacheKey key);
    Task<Dictionary<DeckWordCacheKey, DeckWord?>> GetManyAsync(IReadOnlyList<DeckWordCacheKey> keys);
    Task SetAsync(DeckWordCacheKey key, DeckWord word, bool fireAndForget = false);
    Task SetManyAsync(IReadOnlyList<(DeckWordCacheKey key, DeckWord word)> entries, bool fireAndForget = false);
}

public record DeckWordCacheKey(string Text, PartOfSpeech PartOfSpeech, string DictionaryForm, string Reading, bool IsPersonNameContext, bool IsNameLikeSudachiNoun);
