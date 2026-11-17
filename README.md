# kb-mcp-demo

> 一份 Markdown 知识库 + 一个零依赖的 C# MCP 服务，让 TRAE、Qoder、WorkBuddy、Claude Desktop 这些 AI 工具共用同一份记忆。

换一个 AI 就要把项目背景、代码规范、踩过的坑重讲一遍？本仓库把这套「跨 Agent 知识库」做成了可以直接跑起来的最小示例：**数据是你的 Markdown 文件，通道是一个 stdio MCP 服务，规则是一段写进各工具全局配置的协议。**

- 服务实现：C# / .NET 10，标准库实现 JSON-RPC 2.0，零第三方依赖
- 数据格式：纯 Markdown + front-matter，Obsidian 可以直接打开当 vault
- 写入安全：每次写入自动 `git commit`，可 diff、可回滚
- 检索方式：逐文件扫描 + 加权计分，几百到几千条规模无需数据库

## 它解决什么问题

各 AI 工具默认各存各的记忆：TRAE 写自己的项目记忆与会话流水，Qoder 写 `MEMORY.md`，WorkBuddy 又一套。格式不同、目录不同，互不可见。结果是同一个结论要在三个工具里各说一遍，切换工具即失忆。

本仓库把「记忆」从工具里抽出来，变成一份独立的、可由任何工具读写的知识库。

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│   TRAE CN    │  │   Qoder CN   │  │  WorkBuddy   │
└──────┬───────┘  └──────┬───────┘  └──────┬───────┘
       │   MCP (stdio JSON-RPC，同一份配置)
       └─────────────────┼─────────────────┘
                    ┌────▼─────┐
                    │ kb-server│  5 个工具：index / search / get / upsert / log
                    └────┬─────┘
                    ┌────▼─────────────────────┐
                    │ kb-demo/  Markdown + git │  唯一真源
                    └──────────────────────────┘
```

## 目录结构

```
kb-mcp-demo/
├── kb-server/               MCP 服务源码
│   ├── Program.cs           stdio 循环、JSON-RPC 分发、工具定义
│   ├── KbStore.cs           条目读写、front-matter 解析、检索计分
│   ├── KbGit.cs             写入后自动 git commit
│   ├── KbLogger.cs          文件日志（stdout 只能留给协议报文）
│   └── KbMcp.csproj
├── kb-demo/                 示例知识库（虚构数据，可整体替换）
│   ├── INDEX.md             唯一入口，≤200 行
│   ├── profile/             跨项目画像与编码规范
│   ├── domain/              领域知识
│   ├── projects/demo-app/   项目长期约定
│   └── archive/             流水，由 kb_log 自动创建
└── README.md
```

## 快速开始

### 1. 编译

```powershell
git clone https://gitee.com/lixiaoliyubo/kb-mcp-demo.git
cd kb-mcp-demo
dotnet build .\kb-server\KbMcp.csproj -c Release
```

产物在 `kb-server\bin\Release\net10.0\KbMcp.dll`。

### 2. 注册到 AI 工具

把下面的路径替换成你的实际克隆路径。**三处必须指向同一个 dll 和同一个 `KB_ROOT`**，这是多个工具能共用记忆的前提。

Claude Desktop（`claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "kb": {
      "command": "dotnet",
      "args": ["D:\\Projects\\kb-mcp-demo\\kb-server\\bin\\Release\\net10.0\\KbMcp.dll"],
      "env": { "KB_ROOT": "D:\\Projects\\kb-mcp-demo\\kb-demo" }
    }
  }
}
```

Cursor / Qoder / WorkBuddy 等支持 `mcpServers` 的工具同理，字段一致。TRAE CN 在设置里的 MCP 面板添加：命令 `dotnet`，参数填 dll 路径，环境变量加 `KB_ROOT`。

### 3. 验证

在 AI 对话框里直接说：

```
读一下知识库索引
```

宿主会调用 `kb_index` 并返回 `kb-demo/INDEX.md` 的内容。再试：

```
在知识库里搜一下 MCP 的 stdio 约束
```

返回 5 条以内的命中结果，带 id、得分与命中片段。

## 提供的 5 个工具

| 工具 | 参数 | 用途 |
|---|---|---|
| `kb_index` | 无 | 读 `INDEX.md`，开工前先调用，判断是否已有相关条目 |
| `kb_search` | `query` / `scope?` / `topK?` | 关键词检索，返回 id、标题、标签、得分、命中片段 |
| `kb_get` | `id` | 按 id 读全文，id 就是相对路径去掉 `.md` |
| `kb_upsert` | `id` / `title` / `content` / `tags?` / `scope?` | 新建或覆盖条目，自动补 front-matter、登记索引、git commit |
| `kb_log` | `title` / `content` / `scope?` | 追加当日流水到 `archive/yyyy-MM/` |

原始 JSON-RPC 报文长这样（可以直接用管道手工测试）：

```jsonc
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"kb_search","arguments":{"query":"MCP stdio","topK":3}}}
```

```powershell
# 手工发一条请求
'{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"kb_index","arguments":{}}}' |
  dotnet .\kb-server\bin\Release\net10.0\KbMcp.dll
```

## 让 AI 真的会用：写进全局规则

MCP 只解决「怎么读写」，不解决「什么时候读写」。各工具的全局规则里需要一段协议，内容要短，且必须带绝对路径：

```text
知识库协议
1. 唯一真源：<KB_ROOT>（git 仓库），INDEX.md 是入口且 ≤200 行
2. 开工前先读 INDEX.md，按标签定位条目再读正文，禁止整库加载
3. 涉及历史决策/约定/踩坑时调用 kb_search，不凭记忆猜
4. 收工前用 kb_upsert 写回稳定结论（id/title/content/tags/scope），过程性结论用 kb_log
5. 各工具自带 memory 仅作本地缓存，事实以知识库为准
```

这三层分工是：**数据层**（git + Markdown）管事实，**接入层**（MCP）管读写，**规则层**（全局规则）管时机。少一层都会退化回「各说各话」。

## 设计取舍

**为什么不用目录 junction 把各工具的记忆目录链接到一起？**
各工具写入格式不同（TRAE 写 jsonl 会话流水与 `project_memory.md`，Qoder/WorkBuddy 写 `MEMORY.md`），直接链接同一目录会互相覆盖、互相污染。链接只适合一次性导入存量。

**为什么不上向量检索？**
条目在千级以内时，逐文件扫描 + 加权计分（标题命中 ×5、标签 ×3、正文 ×1）已经足够快，且完全可解释、零依赖。中文按子串命中，不需要分词器。条目过千再换 SQLite FTS5，再往后才考虑向量。

**为什么假定了 git？**
每次写入自动 commit，等于给知识库加了免费的时间机器：任何一条被 AI 改坏的结论都能 diff 出来、回滚回去。没有 git 时服务会降级为纯文件写入，不会报错。

## 治理规则（决定能不能长期用）

1. **单写通道**：只经 MCP 写入，禁止手改，保证每次变更都有提交记录。
2. **晋升**：`archive/` 里的流水被复用 ≥2 次才提升到 `projects/` 或 `domain/`，否则索引会变成垃圾场。
3. **索引预算**：`INDEX.md` ≤200 行，超了按 scope 拆成 `INDEX-<scope>.md`。
4. **过期**：`updated` 超过 90 天的决策标 `stale`，检索时降权。
5. **人工层**：Obsidian 打开同一目录当 vault，人和 AI 共用同一份文件。
6. **敏感信息不入库**：密钥、令牌、客户隐私写库前先扫描。

## 已知边界

- Agent 不会主动遵守协议，除非规则里写清了路径与调用时机，所以协议要短、要有绝对路径。
- 各工具自带的 memory 仍会继续写，只能降级为「本地缓存」，靠冲突规则收敛。
- `kb_upsert` 是整篇覆盖，不做字段级合并；需要局部修改时先 `kb_get` 再整篇写回。

## License

MIT
