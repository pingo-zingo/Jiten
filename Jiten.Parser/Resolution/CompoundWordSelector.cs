using Jiten.Parser;
using Jiten.Core.Data;
using Jiten.Parser.Data;

namespace Jiten.Parser.Resolution;

internal static class CompoundWordSelector
{
    public static (int? expressionWordId, int? compoundWordId) FindCompoundWordIds(
        List<int> wordIds, Dictionary<int, JmDictWordMeta> wordMeta, bool isKana = true)
        => FindCompoundWordIds(wordIds, new InMemoryWordMetaIndex(wordMeta), isKana);



    public static (int? expressionWordId, int? compoundWordId) FindCompoundWordIds(
        List<int> wordIds,
        IWordMetaIndex wordMeta,
        bool isKana = true)
    {
        wordMeta.PrefetchWordMetadata(wordIds);
        int? bestExprWordId = null;
        int bestExprScore = int.MinValue;
        int? bestCompWordId = null;
        int bestCompScore = int.MinValue;

        foreach (var wordId in wordIds)
        {
            if (!wordMeta.TryGetValue(wordId, out var meta)) continue;

            if (Array.IndexOf(meta.Pos, PartOfSpeech.Expression) >= 0)
            {
                int score = meta.GetPriorityScore(isKana);
                if (score > bestExprScore || (score == bestExprScore && (bestExprWordId == null || wordId < bestExprWordId.Value)))
                {
                    bestExprScore = score;
                    bestExprWordId = wordId;
                }
            }

            bool hasValidPos = false;
            foreach (var p in meta.Pos)
            {
                if (PosMapper.IsValidCompoundPOS(p))
                {
                    hasValidPos = true;
                    break;
                }
            }

            if (hasValidPos)
            {
                int score = meta.GetPriorityScore(isKana);
                if (score > bestCompScore || (score == bestCompScore && (bestCompWordId == null || wordId < bestCompWordId.Value)))
                {
                    bestCompScore = score;
                    bestCompWordId = wordId;
                }
            }
        }

        return (bestExprWordId, bestCompWordId);
    }
}
