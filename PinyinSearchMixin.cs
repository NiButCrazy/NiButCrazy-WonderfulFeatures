using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PCL.Mixin;
using TinyPinyin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 本地 Mod 列表拼音搜索。
/// 按中文片段生成全拼、首字母和混合别名，但只有查询词是某个别名中的连续子串时才启用，
/// 因此支持 yyny、yingyongny、jixiedl，同时 yyy 这种跳字模糊不会命中。
/// 原有中文/英文搜索仍保持 PCL 自带的模糊匹配行为。
/// </summary>
[Mixin("PCL.PageInstanceCompResource")]
internal static class PinyinSearchMixin
{
    private sealed record PinyinInfo(string[] Aliases);

    private static readonly ConcurrentDictionary<string, PinyinInfo> PinyinCache =
        new(StringComparer.Ordinal);

    [Overwrite("GetSearchResult")]
    private static List<ModLocalComp.LocalCompFile> GetSearchResult(
        [This] PageInstanceCompResource instance,
        [Arg(0)] string query)
        => Search(instance, query);

    internal static List<ModLocalComp.LocalCompFile> Search(
        PageInstanceCompResource instance,
        string query)
    {
        try
        {
            var output = ModLocalComp.compResourceListLoader.output;
            if (output is null) return [];

            var queryParts = query
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.ToLowerInvariant())
                .ToArray();

            var queryList = new List<ModBase.SearchEntry<ModLocalComp.LocalCompFile>>();
            var nameAliasesByEntry = new Dictionary<ModLocalComp.LocalCompFile, string[]>();

            foreach (var entry in output.AsReadOnly())
            {
                var searchSource = new List<ModBase.SearchSource>();
                var nameAliases = new List<string>();

                void AddNameSource(string? text, double weight)
                {
                    var source = MakeSource(text, weight, queryParts);
                    searchSource.Add(source);
                    nameAliases.AddRange(source.aliases);
                }

                AddNameSource(entry.Name, 1d);
                AddNameSource(entry.FileName, 1d);

                if (entry.Version is not null)
                    searchSource.Add(new ModBase.SearchSource(entry.Version, 0.2d));
                if (!string.IsNullOrEmpty(entry.Description))
                    searchSource.Add(new ModBase.SearchSource(entry.Description, 0.4d));

                if (entry.Comp is not null)
                {
                    if ((entry.Comp.RawName ?? "") != (entry.Name ?? ""))
                        AddNameSource(entry.Comp.RawName, 1d);
                    if ((entry.Comp.TranslatedName ?? "") != (entry.Comp.RawName ?? ""))
                        AddNameSource(entry.Comp.TranslatedName, 1d);
                    var compDescription = entry.Comp.Description;
                    if (!string.IsNullOrEmpty(compDescription) &&
                        compDescription != (entry.Description ?? ""))
                        searchSource.Add(new ModBase.SearchSource(compDescription, 0.4d));
                    searchSource.Add(new ModBase.SearchSource(string.Join("", entry.Comp.Tags), 0.2d));
                }

                nameAliasesByEntry[entry] = nameAliases.ToArray();
                queryList.Add(new ModBase.SearchEntry<ModLocalComp.LocalCompFile>
                    { item = entry, searchSource = searchSource });
            }

            var results = ModBase.Search(queryList, query, ModBase.MaxLocalSearchDepth, 0.35d);

            // 模糊匹配可以宽松，但不允许查询里出现名称级字段中不存在的字符。
            // 描述和标签只参与打分，不能为错字背书。
            return results
                .Where(result => result.absoluteRight ||
                                 (nameAliasesByEntry.TryGetValue(result.item, out var aliases) &&
                                  ContainsAllQueryChars(aliases, queryParts)))
                .Select(result => result.item).ToList();
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("拼音搜索异常：" + ex);
            throw;
        }
    }

    private static ModBase.SearchSource MakeSource(
        string? text,
        double weight,
        IReadOnlyList<string> queryParts)
    {
        if (string.IsNullOrEmpty(text))
            return new ModBase.SearchSource(text ?? string.Empty, weight);

        var pinyin = PinyinCache.GetOrAdd(text, BuildPinyinInfo);
        if (queryParts.Count == 0 || pinyin.Aliases.Length == 0)
            return new ModBase.SearchSource(text, weight);

        var aliases = new List<string>(pinyin.Aliases.Length + 1) { text };
        foreach (var alias in pinyin.Aliases)
        {
            // 关键：查询的每一段都必须在该别名里连续出现。
            // 这样混合写法可以命中，而 yyy 这种跨字符跳字不会被加入。
            if (queryParts.All(part => alias.Contains(part, StringComparison.Ordinal)))
                AddUnique(aliases, alias);
        }

        return new ModBase.SearchSource(aliases.ToArray(), weight);
    }

    private static void AddUnique(List<string> aliases, string value)
    {
        if (!aliases.Contains(value, StringComparer.Ordinal))
            aliases.Add(value);
    }

    /// <summary>
    /// 模糊结果必须满足：查询的每个词，其字符都能在某个名称级别名中找到，
    /// 且重复字符的数量不能超过别名中的数量。允许跳字，不允许错字/缺字。
    /// </summary>
    private static bool ContainsAllQueryChars(
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> queryParts)
    {
        foreach (var part in queryParts)
        {
            if (part.Length == 0) continue;
            if (!aliases.Any(alias => AliasContainsAllChars(alias, part)))
                return false;
        }

        return true;
    }

    private static bool AliasContainsAllChars(string? alias, string queryPart)
    {
        if (string.IsNullOrEmpty(alias)) return false;

        alias = alias.Replace(" ", "").ToLowerInvariant();
        var needed = new Dictionary<char, int>();
        foreach (var c in queryPart)
            needed[c] = needed.GetValueOrDefault(c) + 1;

        foreach (var pair in needed)
        {
            var count = 0;
            foreach (var c in alias)
                if (c == pair.Key)
                    count++;
            if (count < pair.Value) return false;
        }

        return true;
    }

    /// <summary>
    /// 按文本中的中文片段分别生成拼音别名。
    /// 例如「应用能源2 (Applied Energistics 2)」只会用「应用能源」生成 yyny / yingyongny，
    /// 数字和英文仍由原始文本匹配。
    /// </summary>
    private static PinyinInfo BuildPinyinInfo(string text)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var run = new StringBuilder();
        foreach (var c in text)
        {
            if (PinyinHelper.IsChinese(c))
            {
                run.Append(c);
                continue;
            }

            AddRunAliases(run.ToString(), aliases);
            run.Clear();
        }

        AddRunAliases(run.ToString(), aliases);
        return new PinyinInfo(aliases.ToArray());
    }

    /// <summary>
    /// 每个分割点之前用全拼、之后用首字母；以及反过来。
    /// 这样 yingyongny（应用 + 能源首字母）和 jixiedl（机械 + 动力首字母）都能覆盖。
    /// </summary>
    private static void AddRunAliases(string text, HashSet<string> aliases)
    {
        if (string.IsNullOrEmpty(text)) return;

        var syllables = PinyinHelper.GetPinyin(text, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(syllable => syllable.ToLowerInvariant())
            .Where(syllable => syllable.Length > 0)
            .ToArray();
        if (syllables.Length == 0) return;

        var count = syllables.Length;
        var suffixFull = new string[count + 1];
        var suffixInitials = new string[count + 1];
        suffixFull[count] = string.Empty;
        suffixInitials[count] = string.Empty;
        for (var i = count - 1; i >= 0; i--)
        {
            suffixFull[i] = syllables[i] + suffixFull[i + 1];
            suffixInitials[i] = syllables[i][0] + suffixInitials[i + 1];
        }

        var prefixFull = new StringBuilder();
        var prefixInitials = new StringBuilder();
        for (var split = 0; split <= count; split++)
        {
            if (split > 0)
            {
                prefixFull.Append(syllables[split - 1]);
                prefixInitials.Append(syllables[split - 1][0]);
            }

            aliases.Add(prefixFull + suffixInitials[split]);
            aliases.Add(prefixInitials + suffixFull[split]);
        }
    }
}
