using System.Text;

namespace KbMcp;

/// <summary>
/// 知识库存储与检索实现。
/// </summary>
/// <remarks>
/// 数据形态是「一个文件一个条目」的 Markdown + front-matter；
/// 检索采用逐文件扫描 + 加权计分（标题×5、标签×3、正文×1），中文按子串命中，不依赖外部分词。
/// </remarks>
public sealed class KbStore
{
    private static readonly char[] QuerySeparators =
    [
        ' ', '\t', '\n', '\r', ',', '，', '.', '。', '?', '？', '!', '！', ':', '：',
        ';', '；', '/', '\\', '|', '(', ')', '（', '）', '[', ']', '"', '\'', '·', '、'
    ];

    private readonly string _root;
    private readonly string _rootFull;

    /// <summary>
    /// 初始化知识库存储实例。
    /// </summary>
    /// <param name="root">知识库根目录。</param>
    private KbStore(string root)
    {
        _root = root;
        _rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 创建存储实例，优先读取环境变量 KB_ROOT。
    /// </summary>
    /// <returns>
    /// 知识库存储实例。
    /// </returns>
    public static KbStore Create()
    {
        try
        {
            // 优先使用 MCP 配置里注入的 KB_ROOT，未配置时回落到当前目录下的 kb-demo
            var root = Environment.GetEnvironmentVariable("KB_ROOT");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Directory.GetCurrentDirectory(), "kb-demo");
            }

            // 根目录不存在说明配置错误，直接抛出便于定位
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"知识库根目录不存在: {root}");
            }

            KbLogger.Info($"知识库根目录: {root}");
            return new KbStore(root);
        }
        catch (Exception ex)
        {
            KbLogger.Error("初始化知识库失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 读取 INDEX.md 索引内容。
    /// </summary>
    /// <returns>
    /// 索引全文；不存在时返回提示文本。
    /// </returns>
    public string ReadIndex()
    {
        try
        {
            var indexPath = Path.Combine(_root, "INDEX.md");
            if (!File.Exists(indexPath))
            {
                return "未找到 INDEX.md，请先创建知识库索引。";
            }

            return File.ReadAllText(indexPath);
        }
        catch (Exception ex)
        {
            KbLogger.Error("读取索引失败", ex);
            return $"读取索引失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 按关键词检索知识库条目。
    /// </summary>
    /// <param name="query">检索关键词，支持空格分隔多个词。</param>
    /// <param name="scope">限定范围（profile/project/domain/archive），为空表示全部。</param>
    /// <param name="topK">返回条数上限。</param>
    /// <returns>
    /// 按相关度倒序排列的检索结果。
    /// </returns>
    public IReadOnlyList<KbEntry> Search(string query, string? scope, int topK)
    {
        try
        {
            // 拆词，空查询直接返回空结果
            var tokens = Tokenize(query);
            if (tokens.Length == 0)
            {
                return [];
            }

            var results = new List<KbEntry>();

            // 逐文件扫描，条目规模在千级以内足够快
            foreach (var file in EnumerateEntries(scope))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var (meta, body) = ParseFrontMatter(text);
                    var title = meta.TryGetValue("title", out var t) ? t : Path.GetFileNameWithoutExtension(file);
                    var tags = ParseTags(meta.TryGetValue("tags", out var rawTags) ? rawTags : string.Empty);

                    // 分项计分：标题权重最高，其次标签，正文最低
                    var score = 0;
                    var hit = false;
                    foreach (var token in tokens)
                    {
                        var titleHits = CountHits(title, token);
                        var tagHits = tags.Any(x => x.Contains(token, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                        var bodyHits = CountHits(body, token);

                        if (titleHits + tagHits + bodyHits == 0)
                        {
                            continue;
                        }

                        hit = true;
                        score += Math.Min(titleHits, 3) * 5 + tagHits * 3 + Math.Min(bodyHits, 5);
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    results.Add(new KbEntry
                    {
                        Id = meta.TryGetValue("id", out var id) ? id : ToId(file),
                        Title = title,
                        Tags = string.Join(",", tags),
                        Updated = meta.TryGetValue("updated", out var updated) ? updated : string.Empty,
                        Score = score,
                        Snippet = BuildSnippet(body, tokens),
                        Path = file
                    });
                }
                catch (Exception ex)
                {
                    // 单个文件读失败不影响整体检索
                    KbLogger.Error($"检索文件失败: {file}", ex);
                }
            }

            return results
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Updated, StringComparer.Ordinal)
                .Take(Math.Clamp(topK, 1, 50))
                .ToList();
        }
        catch (Exception ex)
        {
            KbLogger.Error("检索知识库失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 按条目 id 读取全文。
    /// </summary>
    /// <param name="id">条目 id，即相对路径去掉 .md，例如 projects/xxx/project。</param>
    /// <returns>
    /// 条目全文；不存在时返回 null。
    /// </returns>
    public string? Get(string id)
    {
        try
        {
            var path = ResolvePath(id);
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            KbLogger.Error($"读取条目失败: {id}", ex);
            throw;
        }
    }

    /// <summary>
    /// 新建或覆盖知识库条目。
    /// </summary>
    /// <param name="id">条目 id，例如 domain/payment。</param>
    /// <param name="title">条目标题。</param>
    /// <param name="body">条目正文（不含 front-matter）。</param>
    /// <param name="tags">标签集合。</param>
    /// <param name="scope">条目范围，未指定时按 id 前缀推断。</param>
    /// <returns>
    /// 写入的文件绝对路径。
    /// </returns>
    public string Upsert(string id, string title, string body, IReadOnlyList<string> tags, string? scope)
    {
        try
        {
            var path = ResolvePath(id);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException($"条目 id 非法: {id}");
            }

            Directory.CreateDirectory(directory);

            // 覆盖写时保留原有来源，便于追溯这条知识的出处
            var source = "agent";
            if (File.Exists(path))
            {
                var (oldMeta, _) = ParseFrontMatter(File.ReadAllText(path));
                if (oldMeta.TryGetValue("source", out var oldSource) && !string.IsNullOrWhiteSpace(oldSource))
                {
                    source = oldSource;
                }
            }

            var finalScope = string.IsNullOrWhiteSpace(scope) ? InferScope(id) : scope!;
            var updated = DateTime.Now.ToString("yyyy-MM-dd");

            // 统一 front-matter 结构，正文保持调用方原样
            var builder = new StringBuilder();
            builder.AppendLine("---");
            builder.AppendLine($"id: {id}");
            builder.AppendLine($"title: {title}");
            builder.AppendLine($"tags: [{string.Join(", ", tags)}]");
            builder.AppendLine($"scope: {finalScope}");
            builder.AppendLine($"updated: {updated}");
            builder.AppendLine($"source: {source}");
            builder.AppendLine("---");
            builder.AppendLine();
            builder.Append(body.TrimEnd());
            builder.AppendLine();

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            KbLogger.Info($"写入条目: {id} -> {path}");

            // 自动登记索引并提交，保证索引与内容不脱节
            RegisterInIndex(id, title, tags, updated);
            KbGit.Commit(_root, [path, Path.Combine(_root, "INDEX.md")], $"kb: upsert {id}");

            return path;
        }
        catch (Exception ex)
        {
            KbLogger.Error($"写入条目失败: {id}", ex);
            throw;
        }
    }

    /// <summary>
    /// 追加当日流水到 archive/yyyy-MM 目录。
    /// </summary>
    /// <param name="title">流水标题。</param>
    /// <param name="body">流水正文。</param>
    /// <param name="scope">关联范围标识，写入 front-matter。</param>
    /// <returns>
    /// 写入的文件绝对路径。
    /// </returns>
    public string AppendLog(string title, string body, string? scope)
    {
        try
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_root, "archive", now.ToString("yyyy-MM"));
            Directory.CreateDirectory(directory);

            // 同一天同标题复用同一文件，避免流水碎片化
            var path = Path.Combine(directory, $"{now:yyyy-MM-dd}-{Slugify(title)}.md");
            var encoding = new UTF8Encoding(false);

            if (File.Exists(path))
            {
                File.AppendAllText(path, Environment.NewLine + body.TrimEnd() + Environment.NewLine, encoding);
            }
            else
            {
                var builder = new StringBuilder();
                builder.AppendLine("---");
                builder.AppendLine($"id: archive/{now:yyyy-MM}/{now:yyyy-MM-dd}-{Slugify(title)}");
                builder.AppendLine($"title: {title}");
                builder.AppendLine($"tags: [流水]");
                builder.AppendLine("scope: archive");
                builder.AppendLine($"updated: {now:yyyy-MM-dd}");
                builder.AppendLine("source: agent");
                builder.AppendLine("---");
                builder.AppendLine();
                builder.AppendLine($"# {now:yyyy-MM-dd} {title}");
                builder.AppendLine();
                builder.Append(body.TrimEnd());
                builder.AppendLine();
                File.WriteAllText(path, builder.ToString(), encoding);
            }

            KbLogger.Info($"追加流水: {title} -> {path}");
            _ = scope; // 关联范围暂只用于日志语义，不影响落盘位置
            KbGit.Commit(_root, [path], $"kb: log {title}");

            return path;
        }
        catch (Exception ex)
        {
            KbLogger.Error($"追加流水失败: {title}", ex);
            throw;
        }
    }

    /// <summary>
    /// 枚举指定范围内的所有条目文件。
    /// </summary>
    /// <param name="scope">范围标识，为空表示全部业务目录。</param>
    /// <returns>
    /// 条目文件的绝对路径集合。
    /// </returns>
    private IEnumerable<string> EnumerateEntries(string? scope)
    {
        var folders = new List<string>();

        // 按范围映射到目录；镜像归档目录不参与检索
        switch ((scope ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "all":
                folders.Add(Path.Combine(_root, "profile"));
                folders.Add(Path.Combine(_root, "projects"));
                folders.Add(Path.Combine(_root, "domain"));
                folders.Add(Path.Combine(_root, "archive"));
                break;
            case "profile":
                folders.Add(Path.Combine(_root, "profile"));
                break;
            case "project":
            case "projects":
                folders.Add(Path.Combine(_root, "projects"));
                break;
            case "domain":
                folders.Add(Path.Combine(_root, "domain"));
                break;
            case "archive":
                folders.Add(Path.Combine(_root, "archive"));
                break;
            default:
                throw new ArgumentException($"未知的检索范围: {scope}");
        }

        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories))
            {
                // 索引与说明文件自身不作为检索结果
                var name = Path.GetFileName(file);
                if (name.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    /// 把 id 解析为受控范围内的文件路径，防止越界写入。
    /// </summary>
    /// <param name="id">条目 id。</param>
    /// <returns>
    /// 文件绝对路径。
    /// </returns>
    private string ResolvePath(string id)
    {
        // 统一分隔符并做基础校验
        var normalized = (id ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (normalized.Length == 0 || normalized.Contains(".."))
        {
            throw new ArgumentException($"条目 id 非法: {id}");
        }

        var full = Path.GetFullPath(Path.Combine(_root, normalized + ".md"));

        // 必须落在知识库根目录内
        if (!full.StartsWith(_rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"条目 id 越界: {id}");
        }

        return full;
    }

    /// <summary>
    /// 把文件路径还原成条目 id。
    /// </summary>
    /// <param name="file">文件绝对路径。</param>
    /// <returns>
    /// 条目 id。
    /// </returns>
    private string ToId(string file)
    {
        var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
        return relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? relative[..^3]
            : relative;
    }

    /// <summary>
    /// 从 id 前缀推断条目范围。
    /// </summary>
    /// <param name="id">条目 id。</param>
    /// <returns>
    /// 范围标识。
    /// </returns>
    private static string InferScope(string id)
    {
        var prefix = (id ?? string.Empty).Split('/')[0].ToLowerInvariant();
        return prefix switch
        {
            "profile" => "profile",
            "projects" => "project",
            "domain" => "domain",
            "archive" => "archive",
            _ => "domain"
        };
    }

    /// <summary>
    /// 解析 front-matter 与正文。
    /// </summary>
    /// <param name="text">文件全文。</param>
    /// <returns>
    /// 元数据字典与正文。
    /// </returns>
    private static (Dictionary<string, string> Meta, string Body) ParseFrontMatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text) || !text.TrimStart().StartsWith("---", StringComparison.Ordinal))
        {
            return (meta, text);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            return (meta, text);
        }

        // 逐行解析 key: value，够用且容错
        for (var i = 1; i < end; i++)
        {
            var index = lines[i].IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            var key = lines[i][..index].Trim();
            var value = lines[i][(index + 1)..].Trim();
            if (key.Length > 0)
            {
                meta[key] = value;
            }
        }

        return (meta, string.Join('\n', lines.Skip(end + 1)));
    }

    /// <summary>
    /// 解析标签字段。
    /// </summary>
    /// <param name="raw">形如 [a, b] 的原始值。</param>
    /// <returns>
    /// 标签集合。
    /// </returns>
    private static List<string> ParseTags(string raw)
    {
        try
        {
            var value = (raw ?? string.Empty).Trim().Trim('[', ']');
            if (value.Length == 0)
            {
                return [];
            }

            return value
                .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            KbLogger.Error($"解析标签失败: {raw}", ex);
            return [];
        }
    }

    /// <summary>
    /// 把查询串拆成关键词。
    /// </summary>
    /// <param name="query">原始查询串。</param>
    /// <returns>
    /// 去重后的关键词数组。
    /// </returns>
    private static string[] Tokenize(string query)
    {
        try
        {
            return (query ?? string.Empty)
                .Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            KbLogger.Error("拆词失败", ex);
            return [];
        }
    }

    /// <summary>
    /// 统计关键词在文本中出现次数（忽略大小写）。
    /// </summary>
    /// <param name="text">目标文本。</param>
    /// <param name="token">关键词。</param>
    /// <returns>
    /// 出现次数。
    /// </returns>
    private static int CountHits(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return 0;
        }

        var count = 0;
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            start = index + token.Length;
        }

        return count;
    }

    /// <summary>
    /// 生成命中片段，供 agent 快速判断是否值得读全文。
    /// </summary>
    /// <param name="body">正文。</param>
    /// <param name="tokens">关键词集合。</param>
    /// <returns>
    /// 截断后的片段文本。
    /// </returns>
    private static string BuildSnippet(string body, string[] tokens)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (tokens.Any(x => trimmed.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "…";
                }
            }

            // 没有命中行时退化为前 160 字
            var plain = body.Trim().Replace("\n", " ");
            return plain.Length <= 160 ? plain : plain[..160] + "…";
        }
        catch (Exception ex)
        {
            KbLogger.Error("生成片段失败", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 把标题转成适合做文件名的 slug。
    /// </summary>
    /// <param name="title">原始标题。</param>
    /// <returns>
    /// 安全 slug。
    /// </returns>
    private static string Slugify(string title)
    {
        var builder = new StringBuilder();
        foreach (var ch in (title ?? string.Empty).Trim())
        {
            // 保留中文、字母、数字，其余统一替换为连字符
            if (char.IsLetterOrDigit(ch) || ch > 0x4E00)
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "entry" : slug;
    }

    /// <summary>
    /// 把新条目登记到 INDEX.md，避免索引与内容脱节。
    /// </summary>
    /// <param name="id">条目 id。</param>
    /// <param name="title">条目标题。</param>
    /// <param name="tags">标签集合。</param>
    /// <param name="updated">更新日期。</param>
    private void RegisterInIndex(string id, string title, IReadOnlyList<string> tags, string updated)
    {
        try
        {
            var indexPath = Path.Combine(_root, "INDEX.md");
            var content = File.Exists(indexPath) ? File.ReadAllText(indexPath) : "# 知识库索引" + Environment.NewLine;

            // 已登记则不重复插入
            if (content.Contains($"| {id} |", StringComparison.Ordinal))
            {
                return;
            }

            const string header = "## 自动登记";
            var row = $"| {id} | {title} | {string.Join(",", tags)} | {updated} |";

            if (!content.Contains(header, StringComparison.Ordinal))
            {
                content = content.TrimEnd() + Environment.NewLine + Environment.NewLine +
                          header + Environment.NewLine + Environment.NewLine +
                          "| id | 摘要 | 标签 | updated |" + Environment.NewLine +
                          "|---|---|---|---|" + Environment.NewLine;
            }

            content = content.TrimEnd() + Environment.NewLine + row + Environment.NewLine;
            File.WriteAllText(indexPath, content, new UTF8Encoding(false));
            KbLogger.Info($"索引登记: {id}");
        }
        catch (Exception ex)
        {
            // 索引登记失败不影响条目本身落盘
            KbLogger.Error($"索引登记失败: {id}", ex);
        }
    }
}

/// <summary>
/// 检索结果条目。
/// </summary>
public sealed class KbEntry
{
    /// <summary>
    /// 条目 id。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 条目标题。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 标签，逗号分隔。
    /// </summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// 最近更新日期。
    /// </summary>
    public string Updated { get; set; } = string.Empty;

    /// <summary>
    /// 相关度得分。
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// 命中片段。
    /// </summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// 文件绝对路径。
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
