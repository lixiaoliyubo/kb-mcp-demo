# 知识库索引

> **唯一入口**，≤200 行。只放「id + 一行摘要 + 标签」，正文按需读取，禁止整库加载。
> 写入格式与治理规则见仓库根目录 [README.md](../README.md)。

## profile · 跨项目画像与约定（每次任务都适用）

| id | 摘要 | 标签 | updated |
|---|---|---|---|
| profile/user | 身份、技术栈、常用语言与偏好 | 画像,技术栈 | 2026-11-17 |
| profile/conventions | 代码风格、注释要求、验证口径 | 规范,注释,验证 | 2026-11-17 |

## domain · 跨项目领域知识

| id | 摘要 | 标签 | updated |
|---|---|---|---|
| domain/mcp-basics | MCP 协议的三类原语与 stdio 传输要点 | MCP,协议,stdio | 2026-11-17 |

## projects · 项目长期约定（一个项目一个文件夹）

| id | 摘要 | 标签 | updated |
|---|---|---|---|
| projects/demo-app/project | 示例项目 demo-app 的技术栈、接口约定与踩坑 | Vue3,.NET,demo | 2026-11-17 |

## archive · 流水（复用 2 次以上再晋升到上面）

| id | 摘要 | 标签 | updated |
|---|---|---|---|
| - | 目前为空，`kb_log` 会自动往这里追加 | 流水 | 2026-11-17 |

## 目录结构

```
kb-demo/
├── INDEX.md                 本文件（唯一入口）
├── profile/                 跨项目画像，单文件
├── domain/                  领域知识，一个主题一个 md
├── projects/<slug>/         一项目一文件夹，主文件 project.md
└── archive/<YYYY-MM>/       流水归档，由 kb_log 自动创建
```

## 写入约定（写回时照抄）

```yaml
---
id: <scope>/<slug>            # 例：projects/demo-app/project
title: <一句话标题>
tags: [标签1, 标签2]
scope: profile|project|domain|archive
updated: 2026-11-17
source: TRAE-CN|Qoder-CN|WorkBuddy|human
---
```

新条目写入后必须在上面表格补一行；超过 200 行时按 scope 拆分成 `INDEX-<scope>.md`。
