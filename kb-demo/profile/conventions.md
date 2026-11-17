---
id: profile/conventions
title: 编码与协作规范（示例）
tags: [规范, 注释, 验证, 提交]
scope: profile
updated: 2026-11-17
source: human
---

# 代码风格

1. 命名：C# 用 PascalCase 公开成员、camelCase 私有字段加 `_` 前缀；前端组件用 PascalCase 文件名。
2. 注释：公开方法写 XML 文档注释（`<summary>` / `<param>` / `<returns>`），内部逻辑只在「为什么」不明显时加行内注释。
3. 异常：边界处（网络、文件、外部进程）必须捕获；内部纯计算不包无意义的 try/catch。
4. 依赖：新增 NuGet / npm 包前先确认标准库或现有依赖能否替代。

# 验证口径

- 后端改动：`dotnet build` 通过 + 至少一次真实调用验证返回结构。
- 前端改动：本地起服务 + 浏览器实际操作一遍主流程，不只看编译通过。
- 文档改动：链接能点开，代码块能复制执行。

# 提交规范

- 一次提交只做一件事，信息写「为什么」而不是「改了哪些文件」。
- 不提交 `bin/`、`obj/`、`node_modules/`、日志与本地配置。
