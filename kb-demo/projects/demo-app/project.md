---
id: projects/demo-app/project
title: demo-app 项目长期约定（示例）
tags: [Vue3, .NET, 接口约定, 部署]
scope: project
updated: 2026-11-17
source: human
---

# 项目概况

`demo-app` 是演示用的前后端分离项目：Vue 3 前端 + .NET 8 API + MySQL，用于验证知识库在多项目场景下的组织方式。

# 技术栈与端口

| 组件 | 技术 | 端口 |
|---|---|---|
| 前台 | Vue 3 + Vite | 5173 |
| 后台 | Vue 3 + Element Plus | 5174 |
| API | .NET 8 + SqlSugar | 5201 |
| 数据库 | MySQL 8 | 3306 |

# 接口约定

1. 统一返回 `{ code, message, data }`，`code = 0` 表示成功。
2. 时间字段一律用 ISO 8601 字符串，不传时间戳。
3. 分页参数固定 `page` / `pageSize`，响应带 `total`。

# 踩坑记录

- **中文路径**：构建脚本里对含中文的路径要加引号，否则 MSBuild 会截断。
- **跨域**：开发环境用 Vite proxy，生产环境由 nginx 反向代理，不在后端开 `AllowAnyOrigin`。
- **枚举序列化**：.NET 默认输出数字，前端要字符串就在 `JsonSerializerOptions` 里加 `JsonStringEnumConverter`。

# 相关条目

- 代码风格见 `profile/conventions`
- 身份与偏好见 `profile/user`
