using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KbMcp;

/// <summary>
/// 知识库 MCP 服务入口。
/// </summary>
/// <remarks>
/// 通过 stdio 收发 JSON-RPC 2.0 报文；stdout 只允许输出协议报文，诊断信息统一写日志文件。
/// </remarks>
public static class Program
{
    private const string ServerName = "kb-server";
    private const string ServerVersion = "1.0.0";

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        // 直接输出中文，避免 \uXXXX 转义放大报文体积、影响 agent 阅读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 进程入口，循环读取 stdin 直到流关闭。
    /// </summary>
    /// <param name="args">启动参数，未使用。</param>
    /// <returns>
    /// 进程退出码。
    /// </returns>
    public static int Main(string[] args)
    {
        try
        {
            KbLogger.Info("kb-server 启动");

            // 初始化失败直接退出，避免宿主拿到半可用服务
            var store = KbStore.Create();

            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            string? line;
            while ((line = stdin.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = HandleRequest(line, store);
                if (response is null)
                {
                    continue;
                }

                stdout.WriteLine(response.ToJsonString(ResponseOptions));
                stdout.Flush();
            }

            KbLogger.Info("stdin 关闭，服务退出");
            return 0;
        }
        catch (Exception ex)
        {
            KbLogger.Error("服务异常退出", ex);
            return 1;
        }
    }

    /// <summary>
    /// 处理单条 JSON-RPC 报文。
    /// </summary>
    /// <param name="line">原始报文。</param>
    /// <param name="store">知识库存储实例。</param>
    /// <returns>
    /// 响应报文；通知类请求返回 null。
    /// </returns>
    private static JsonObject? HandleRequest(string line, KbStore store)
    {
        JsonNode? request = null;

        try
        {
            request = JsonNode.Parse(line);
            if (request is not JsonObject obj)
            {
                return BuildError(null, -32700, "报文不是合法的 JSON 对象");
            }

            var method = obj["method"]?.GetValue<string>() ?? string.Empty;
            JsonNode? id = obj["id"]?.DeepClone();

            // 通知类请求不需要响应
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                KbLogger.Info($"收到通知: {method}");
                return null;
            }

            var parameters = obj["params"] as JsonObject ?? new JsonObject();
            KbLogger.Info($"收到请求: {method}");

            switch (method)
            {
                case "initialize":
                    return BuildResult(id, BuildInitializeResult(parameters));

                case "ping":
                    return BuildResult(id, new JsonObject());

                case "tools/list":
                    return BuildResult(id, new JsonObject { ["tools"] = BuildToolDefinitions() });

                case "tools/call":
                    return BuildResult(id, InvokeTool(parameters, store));

                default:
                    return BuildError(id, -32601, $"未实现的方法: {method}");
            }
        }
        catch (Exception ex)
        {
            KbLogger.Error("处理请求失败", ex);
            var id = (request as JsonObject)?["id"]?.DeepClone();
            return BuildError(id, -32603, $"服务内部错误: {ex.Message}");
        }
    }

    /// <summary>
    /// 构造 initialize 的返回体。
    /// </summary>
    /// <param name="parameters">客户端参数，用于回显协议版本。</param>
    /// <returns>
    /// 初始化结果节点。
    /// </returns>
    private static JsonObject BuildInitializeResult(JsonObject parameters)
    {
        // 优先回显客户端请求的协议版本，缺省用 2024-11-05
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        var version = string.IsNullOrWhiteSpace(requested) ? "2024-11-05" : requested;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion
            }
        };
    }

    /// <summary>
    /// 分发 tools/call 请求并归一化返回结构。
    /// </summary>
    /// <param name="parameters">请求参数，含 name 与 arguments。</param>
    /// <param name="store">知识库存储实例。</param>
    /// <returns>
    /// MCP 工具调用结果。
    /// </returns>
    private static JsonObject InvokeTool(JsonObject parameters, KbStore store)
    {
        var toolName = parameters["name"]?.GetValue<string>() ?? string.Empty;
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var text = toolName switch
            {
                "kb_index" => store.ReadIndex(),

                "kb_search" => Search(store, arguments),

                "kb_get" => Get(store, arguments),

                "kb_upsert" => Upsert(store, arguments),

                "kb_log" => Log(store, arguments),

                _ => throw new ArgumentException($"未知工具: {toolName}")
            };

            KbLogger.Info($"工具执行成功: {toolName}");
            return BuildToolResult(text, false);
        }
        catch (Exception ex)
        {
            // 工具级异常回给调用方，不中断服务
            KbLogger.Error($"工具执行失败: {toolName}", ex);
            return BuildToolResult($"{toolName} 执行失败: {ex.Message}", true);
        }
    }

    /// <summary>
    /// 执行 kb_search。
    /// </summary>
    /// <param name="store">知识库存储实例。</param>
    /// <param name="arguments">工具参数。</param>
    /// <returns>
    /// 检索结果文本（JSON）。
    /// </returns>
    private static string Search(KbStore store, JsonObject arguments)
    {
        try
        {
            var query = arguments["query"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("query 不能为空");
            }

            var scope = arguments["scope"]?.GetValue<string>();
            var topK = arguments["topK"]?.GetValue<int>() ?? 5;
            var results = store.Search(query, scope, topK);

            if (results.Count == 0)
            {
                return $"未命中任何条目（query={query}, scope={scope ?? "all"}）。";
            }

            return JsonSerializer.Serialize(results, OutputOptions);
        }
        catch (Exception ex)
        {
            KbLogger.Error("kb_search 失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 执行 kb_get。
    /// </summary>
    /// <param name="store">知识库存储实例。</param>
    /// <param name="arguments">工具参数。</param>
    /// <returns>
    /// 条目全文或未找到提示。
    /// </returns>
    private static string Get(KbStore store, JsonObject arguments)
    {
        try
        {
            var id = arguments["id"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("id 不能为空");
            }

            return store.Get(id) ?? $"未找到条目: {id}";
        }
        catch (Exception ex)
        {
            KbLogger.Error("kb_get 失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 执行 kb_upsert。
    /// </summary>
    /// <param name="store">知识库存储实例。</param>
    /// <param name="arguments">工具参数。</param>
    /// <returns>
    /// 写入结果提示。
    /// </returns>
    private static string Upsert(KbStore store, JsonObject arguments)
    {
        try
        {
            var id = arguments["id"]?.GetValue<string>() ?? string.Empty;
            var title = arguments["title"]?.GetValue<string>() ?? string.Empty;
            var content = arguments["content"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("id 与 title 不能为空");
            }

            // tags 既支持字符串数组，也兼容逗号分隔字符串
            var tags = new List<string>();
            if (arguments["tags"] is JsonArray array)
            {
                tags.AddRange(array.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => x.Length > 0));
            }
            else if (arguments["tags"]?.GetValue<string>() is { Length: > 0 } rawTags)
            {
                tags.AddRange(rawTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            }

            var scope = arguments["scope"]?.GetValue<string>();
            var path = store.Upsert(id, title, content, tags, scope);
            return $"已写入条目 {id} -> {path}";
        }
        catch (Exception ex)
        {
            KbLogger.Error("kb_upsert 失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 执行 kb_log。
    /// </summary>
    /// <param name="store">知识库存储实例。</param>
    /// <param name="arguments">工具参数。</param>
    /// <returns>
    /// 追加结果提示。
    /// </returns>
    private static string Log(KbStore store, JsonObject arguments)
    {
        try
        {
            var title = arguments["title"]?.GetValue<string>() ?? string.Empty;
            var content = arguments["content"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("title 与 content 不能为空");
            }

            var scope = arguments["scope"]?.GetValue<string>();
            var path = store.AppendLog(title, content, scope);
            return $"已追加流水 -> {path}";
        }
        catch (Exception ex)
        {
            KbLogger.Error("kb_log 失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 构造 MCP 工具定义列表。
    /// </summary>
    /// <returns>
    /// 工具定义数组。
    /// </returns>
    private static JsonArray BuildToolDefinitions()
    {
        return
        [
            Tool("kb_index", "读取知识库索引 INDEX.md。开工前先调用，判断是否已有相关条目。", EmptySchema()),
            Tool("kb_search", "按关键词检索知识库，返回 id、标题、命中片段与得分。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = Property("string", "检索关键词，多个词用空格分隔"),
                        ["scope"] = Property("string", "限定范围：profile / project / domain / archive，可省略"),
                        ["topK"] = Property("integer", "返回条数上限，默认 5")
                    },
                    ["required"] = new JsonArray("query")
                }),
            Tool("kb_get", "按条目 id 读取全文，例如 projects/yuandl-toolsapp-web/project。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = Property("string", "条目 id，即相对路径去掉 .md")
                    },
                    ["required"] = new JsonArray("id")
                }),
            Tool("kb_upsert", "新建或覆盖知识库条目；会自动补 front-matter、登记索引并 git commit。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = Property("string", "条目 id，如 domain/payment 或 projects/xxx/lessons"),
                        ["title"] = Property("string", "条目标题"),
                        ["content"] = Property("string", "条目正文（Markdown，不含 front-matter）"),
                        ["tags"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "标签数组，可省略"
                        },
                        ["scope"] = Property("string", "范围：profile / project / domain / archive，可省略（按 id 前缀推断）")
                    },
                    ["required"] = new JsonArray("id", "title", "content")
                }),
            Tool("kb_log", "把当日流水追加到 archive/yyyy-MM/，用于记录过程性结论。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = Property("string", "流水标题"),
                        ["content"] = Property("string", "流水正文（Markdown）"),
                        ["scope"] = Property("string", "关联范围标识，可省略")
                    },
                    ["required"] = new JsonArray("title", "content")
                })
        ];
    }

    /// <summary>
    /// 构造单个工具定义。
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <param name="description">工具说明。</param>
    /// <param name="schema">参数 JSON Schema。</param>
    /// <returns>
    /// 工具定义节点。
    /// </returns>
    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema
        };
    }

    /// <summary>
    /// 构造无参 schema。
    /// </summary>
    /// <returns>
    /// 空对象 schema。
    /// </returns>
    private static JsonObject EmptySchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        };
    }

    /// <summary>
    /// 构造单个参数定义。
    /// </summary>
    /// <param name="type">JSON 类型。</param>
    /// <param name="description">参数说明。</param>
    /// <returns>
    /// 参数 schema 节点。
    /// </returns>
    private static JsonObject Property(string type, string description)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["description"] = description
        };
    }

    /// <summary>
    /// 构造工具调用结果。
    /// </summary>
    /// <param name="text">返回文本。</param>
    /// <param name="isError">是否为错误结果。</param>
    /// <returns>
    /// MCP 结果节点。
    /// </returns>
    private static JsonObject BuildToolResult(string text, bool isError)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            },
            ["isError"] = isError
        };
    }

    /// <summary>
    /// 构造成功响应。
    /// </summary>
    /// <param name="id">请求 id。</param>
    /// <param name="result">结果节点。</param>
    /// <returns>
    /// JSON-RPC 成功响应。
    /// </returns>
    private static JsonObject BuildResult(JsonNode? id, JsonObject result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
    }

    /// <summary>
    /// 构造错误响应。
    /// </summary>
    /// <param name="id">请求 id。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>
    /// JSON-RPC 错误响应。
    /// </returns>
    private static JsonObject BuildError(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }
}
