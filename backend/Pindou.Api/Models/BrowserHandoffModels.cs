// 文件：BrowserHandoffModels.cs
// 用途：声明微信内置浏览器与系统浏览器之间的一次性图纸接力契约。
// 核心职责：接收前端图纸快照，并返回短期接力令牌及失效时间。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-21

using System.Text.Json;

namespace Pindou.Api.Models;

public sealed record BrowserHandoffRequest(JsonElement Project);

public sealed record BrowserHandoffCreated(string Token, DateTimeOffset ExpiresAt);
