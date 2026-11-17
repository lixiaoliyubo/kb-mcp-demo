---
id: domain/mcp-basics
title: MCP 协议要点（示例条目）
tags: [MCP, 协议, stdio, JSON-RPC]
scope: domain
updated: 2026-11-17
source: human
---

# 是什么

MCP（Model Context Protocol）是宿主应用（TRAE、Qoder、Claude Desktop 等）与外部能力进程之间的标准协议，报文是 JSON-RPC 2.0。

# 三类原语

| 原语 | 方向 | 典型用途 |
|---|---|---|
| tools | 模型调用 | 执行动作，如读写文件、查数据库 |
| resources | 宿主读取 | 提供可引用的上下文数据 |
| prompts | 用户选择 | 预置提示词模板 |

本仓库的 `kb-server` 只实现 `tools`，已经够用：读索引、检索、读全文、写入、追加流水。

# stdio 传输的三个硬约束

1. **stdout 只能输出协议报文**，任何调试输出都会让宿主解析失败；诊断信息必须写文件或 stderr。
2. 一条报文一行 JSON，不允许格式化换行；写完立即 flush。
3. 通知类方法（`notifications/*`）不返回响应，返回空内容也算违规。

# 最小方法集

宿主启动后按顺序调用：

```
initialize      → 交换协议版本与能力
tools/list      → 拿到工具定义
tools/call      → 实际调用
```

`ping` 可用来探活，返回空对象即可。

# 常见坑

- 宿主对 `initialize` 的 `protocolVersion` 有要求，直接回显客户端请求的版本最稳。
- 中文返回值如果被转义成 `\uXXXX`，报文体积会翻几倍，序列化时关掉默认转义。
- 工具内部异常要包成 `isError: true` 的结果返回，不要让进程退出，否则宿主认为服务挂了。
