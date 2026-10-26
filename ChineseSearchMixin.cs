using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PCL.Mixin;

namespace PCL.Plugin.NiButCrazy.Mixins;

/// <summary>
/// 改进中文搜索：PCL 原本会把「应用能源」精确匹配到 AE1 的 slug，
/// 在 1.20.1 等版本下搜不到 AE2。这里改为从翻译库统计公共英文关键词
/// （例如 applied energistics），让搜索结果覆盖同一系列的资源。
/// </summary>
[Mixin("PCL.ModComp")]
internal static class ChineseSearchMixin
{
    private static readonly Regex ChineseRegex = new("[一-鿿]", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new("[A-Za-z0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords =
    [
        "the", "of", "for", "mod", "mods", "and", "forge", "fabric", "quilt", "neoforge",
        "edition", "addon", "api", "compat", "compatibility", "unofficial", "legacy",
        "extended", "life", "stuff", "stuffs", "more", "fix", "fixer", "patch", "port",
        "mc", "minecraft"
    ];

    private static readonly PropertyInfo? ConnectionStringProperty = typeof(ModComp).GetProperty(
        "CompDBConnectionString", BindingFlags.NonPublic | BindingFlags.Static);

    [Inject("CompProjectsGet", At = MixinAt.Head)]
    private static void RewriteChineseSearch([Arg(0)] ModLoader.LoaderTask<ModComp.CompProjectRequest, int> task)
    {
        try
        {
            var request = task.input;
            if (request is null) return;

            var query = (request.searchText ?? string.Empty).Trim();
            if (query.Length == 0 || !ChineseRegex.IsMatch(query)) return;
            if (request.type is not (ModComp.CompType.Mod or ModComp.CompType.DataPack or ModComp.CompType.ModPack))
                return;

            var keywords = BuildKeywords(query);
            if (string.IsNullOrWhiteSpace(keywords)) return;

            request.searchText = keywords;
            request.curseForgeAltSearchText = null!;
            PluginBootstrap.WriteLog($"[Search] 中文搜索改写：{query} -> {keywords}");
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("[Search] 中文搜索改写失败：" + ex);
        }
    }

    private static string? BuildKeywords(string query)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        var entries = QueryEntries(connectionString, query);
        if (entries.Count == 0) return null;

        var englishScores = new Dictionary<string, int>(StringComparer.Ordinal);
        var slugScores = new Dictionary<string, int>(StringComparer.Ordinal);
        var perEntryWords = new List<HashSet<string>>();

        foreach (var entry in entries)
        {
            var englishWords = ExtractWords(entry.ChineseName);
            var slugWords = ExtractWords(
                (entry.CurseForgeSlug ?? string.Empty).Replace('-', ' ').Replace('_', ' ') + " " +
                (entry.ModrinthSlug ?? string.Empty).Replace('-', ' ').Replace('_', ' '));

            foreach (var word in englishWords)
                englishScores[word] = englishScores.GetValueOrDefault(word) + 1;
            foreach (var word in slugWords)
                slugScores[word] = slugScores.GetValueOrDefault(word) + 1;

            perEntryWords.Add(new HashSet<string>(englishWords.Concat(slugWords), StringComparer.Ordinal));
        }

        // 优先使用英文名中的词（applied / energistics），
        // slug 缩写（ae2）只用来补充，避免把查询锁死到单个项目。
        var candidates = englishScores.Keys
            .Concat(slugScores.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(word => new
            {
                Word = word,
                EnglishCount = englishScores.GetValueOrDefault(word),
                SlugCount = slugScores.GetValueOrDefault(word),
                EntryCount = perEntryWords.Count(set => set.Contains(word))
            })
            .Where(item => item.EntryCount >= 2)
            .OrderByDescending(item => item.EnglishCount)
            .ThenByDescending(item => item.SlugCount)
            .ThenByDescending(item => item.Word.Length)
            .Take(2)
            .ToList();

        if (candidates.Count > 0)
        {
            var words = new List<string> { candidates[0].Word };
            if (candidates.Count > 1 &&
                candidates[1].EnglishCount >= Math.Max(2, candidates[0].EnglishCount / 2))
                words.Add(candidates[1].Word);
            return string.Join(" ", words);
        }

        // 只有一个或少量匹配条目时，回退到最佳候选的英文关键词。
        var fallback = entries
            .OrderBy(entry => entry.ChineseName.Length)
            .Select(entry => ExtractWords(
                entry.ChineseName + " " + (entry.CurseForgeSlug ?? string.Empty) + " " +
                (entry.ModrinthSlug ?? string.Empty)))
            .FirstOrDefault(words => words.Count > 0);

        return fallback is null ? null : string.Join(" ", fallback.Take(3));
    }

    private static List<CompTranslationEntry> QueryEntries(string connectionString, string query)
    {
        var result = new List<CompTranslationEntry>();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT WikiId, ChineseName, CurseForgeSlug, ModrinthSlug " +
            "FROM ModTranslation WHERE ChineseName LIKE @p ESCAPE '\\'";
        var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        command.Parameters.AddWithValue("@p", $"%{escaped}%");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CompTranslationEntry(
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    private static string? GetConnectionString()
    {
        try
        {
            if (ConnectionStringProperty?.GetValue(null) is string connectionString &&
                !string.IsNullOrWhiteSpace(connectionString))
                return connectionString;
        }
        catch (Exception ex)
        {
            PluginBootstrap.WriteLog("[Search] 获取翻译库连接失败：" + ex);
        }

        try
        {
            var cacheDirectory = Path.Combine(ModBase.pathTemp, "Cache");
            if (!Directory.Exists(cacheDirectory)) return null;
            var database = Directory.GetFiles(cacheDirectory, "ModData*.sqlite")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return database is null ? null : $"Data Source=\"{database}\";Pooling=False";
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractWords(string text)
    {
        var result = new List<string>();
        foreach (Match match in WordRegex.Matches(text))
        {
            var word = match.Value.ToLowerInvariant();
            if (word.Length <= 1) continue;
            if (word.All(char.IsDigit)) continue;
            if (StopWords.Contains(word)) continue;
            if (!result.Contains(word)) result.Add(word);
        }

        return result;
    }

    private sealed record CompTranslationEntry(string ChineseName, string? CurseForgeSlug, string? ModrinthSlug);
}
