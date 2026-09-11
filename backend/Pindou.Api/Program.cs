// 文件：Program.cs
// 用途：配置拼豆服务的依赖、跨域、限流、中间件和 HTTP API 路由。
// 核心职责：校验外部请求、组织目录/量化/导出服务，并提供 Linux 与本地部署入口。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-27

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using MySqlConnector;
using Pindou.Api.Models;
using Pindou.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 可靠性参数统一从配置读取并限制在安全范围，避免错误配置让请求无限等待或连接池无限膨胀。
var defaultRequestTimeout = TimeSpan.FromSeconds(ReadBoundedInt(builder.Configuration, "Reliability:DefaultRequestTimeoutSeconds", 18, 5, 120));
var heartbeatRequestTimeout = TimeSpan.FromSeconds(ReadBoundedInt(builder.Configuration, "Reliability:HeartbeatRequestTimeoutSeconds", 7, 3, 30));
var exportRequestTimeout = TimeSpan.FromSeconds(ReadBoundedInt(builder.Configuration, "Reliability:ExportRequestTimeoutSeconds", 55, 10, 180));
var quantizeRequestTimeout = TimeSpan.FromSeconds(ReadBoundedInt(builder.Configuration, "Reliability:QuantizeRequestTimeoutSeconds", 110, 15, 300));
var quantizeMaxConcurrency = ReadBoundedInt(builder.Configuration, "Reliability:QuantizeMaxConcurrency", 2, 1, 8);
var sseKeepaliveInterval = TimeSpan.FromSeconds(ReadBoundedInt(builder.Configuration, "Reliability:SseKeepaliveSeconds", 15, 5, 60));
var serverMaxConcurrentConnections = ReadBoundedInt(builder.Configuration, "Reliability:ServerMaxConcurrentConnections", 256, 32, 4096);
var serverKeepAliveSeconds = ReadBoundedInt(builder.Configuration, "Reliability:ServerKeepAliveSeconds", 60, 15, 300);
var maxConcurrentApiRequests = ReadBoundedInt(builder.Configuration, "Reliability:MaxConcurrentApiRequests", 48, 8, 512);
var maxConcurrentRequestsPerRoute = ReadBoundedInt(builder.Configuration, "Reliability:MaxConcurrentRequestsPerRoute", 16, 2, 128);

// MySQL 连接字符串在运行时补齐超时、连接池和失效连接探测参数，不在源码中复制账号密码。
// 请求取消令牌负责尽快停止正在执行的 SQL，这些驱动级限制负责数据库半断开时快速失败并回收连接。
var configuredMySql = builder.Configuration.GetConnectionString("MySql");
if (!string.IsNullOrWhiteSpace(configuredMySql))
{
    var mysql = new MySqlConnectionStringBuilder(configuredMySql)
    {
        ConnectionTimeout = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseConnectionTimeoutSeconds", 5, 2, 30),
        DefaultCommandTimeout = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseCommandTimeoutSeconds", 10, 3, 60),
        MinimumPoolSize = 0,
        MaximumPoolSize = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseMaximumPoolSize", 50, 10, 200),
        ConnectionIdleTimeout = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseIdleTimeoutSeconds", 60, 15, 600),
        ConnectionLifeTime = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseConnectionLifetimeSeconds", 300, 60, 3600),
        Keepalive = (uint)ReadBoundedInt(builder.Configuration, "Reliability:DatabaseKeepaliveSeconds", 30, 0, 300),
        ConnectionReset = true,
        Pooling = true
    };
    builder.Configuration["ConnectionStrings:MySql"] = mysql.ConnectionString;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
    // 入站连接设置硬上限，避免异常客户端耗尽文件句柄；SSE 端点另有更细的单端点上限。
    options.Limits.MaxConcurrentConnections = serverMaxConcurrentConnections;
    // 慢速或半开客户端不能无限占用 Kestrel 连接；SSE 响应由应用层保活，不受请求头时限影响。
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(serverKeepAliveSeconds);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 16 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .AllowAnyHeader()
    .AllowAnyMethod()
    // 本机开发时前端直连 5080，需要携带匿名/管理 HttpOnly Cookie；生产环境仍由来源白名单约束。
    .AllowCredentials()
    .SetIsOriginAllowed(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "localhost" or "127.0.0.1")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // 命名策略 "api"：仅对生成/导出等资源接口限流，心跳、SSE、目录等高频/长连接请求不受限，避免多标签页卡顿。
    options.AddPolicy("api", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    // 登录限流：每 IP 5 分钟最多 30 次，防暴力枚举密钥。
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));
    // 云存档写入限流（中-02）：按账号（X-License-Key）+ IP 组合分区，每分钟最多 60 次、队列 1，
    // 防止持有有效会话的异常客户端高频覆盖存档，减轻数据库/磁盘/网络负载。
    options.AddPolicy("save", context =>
    {
        var account = context.Request.Headers["X-License-Key"].FirstOrDefault();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var partitionKey = string.IsNullOrEmpty(account) ? "ip:" + ip : "account:" + account;
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    // 联机编辑是高频、小批量写入，不能与普通 API 共用每分钟 60 次窗口，否则持续绘制会被误限流。
    // 以 IP 为边界限制突发流量；服务端另有单批数量、房间容量、路由并发和成员令牌校验形成纵深保护。
    options.AddPolicy("collab-edit", context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 40,
                TokensPerPeriod = 20,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueLimit = 20,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
});
builder.Services.AddSingleton<FeatureSwitchStore>();
builder.Services.AddSingleton<PaletteCatalog>();
builder.Services.AddSingleton<PatternQuantizer>();
builder.Services.AddSingleton<PatternAggregateQuantizer>();
builder.Services.AddSingleton<ExcelExportService>();
builder.Services.AddSingleton<BrowserHandoffStore>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton<IpTrialTracker>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<CanvasSaveStore>();
builder.Services.AddSingleton<SessionEventHub>();
builder.Services.AddSingleton<SwitchEventHub>();
builder.Services.AddSingleton<KeyStateEventHub>();
builder.Services.AddSingleton<AdminTokenStore>();
builder.Services.AddSingleton<AdminSessionStore>();
builder.Services.AddSingleton<SseTicketStore>();
builder.Services.AddSingleton<AnonymousSessionStore>();
builder.Services.AddSingleton<CollabService>();
builder.Services.AddHostedService<CollabSweeper>();
builder.Services.AddHostedService<AdminTokenRotator>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // 安全：KnownNetworks/KnownProxies 全空时 ASP.NET Core 会信任所有代理的转发头，
    // 攻击者可伪造 X-Forwarded-For 绕过试用隔离、限流或伪造来源 IP。
    // 这里只信任回环地址（本机 nginx/反代与后端同机）与配置 ForwardedHeaders:KnownProxies 显式列出的代理。
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Loopback, 8));        // 127.0.0.0/8
    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.IPv6Loopback, 128)); // ::1
    var configuredProxies = builder.Configuration["ForwardedHeaders:KnownProxies"];
    if (!string.IsNullOrWhiteSpace(configuredProxies))
        foreach (var part in configuredProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IPAddress.TryParse(part, out var addr)) options.KnownProxies.Add(addr);
});

var app = builder.Build();
// 普通 API 的全局与单路由并发槽均采用“零排队”：容量满时立即返回 503，避免请求在队列中占住
// 浏览器、反向代理或数据库连接直到各层超时。路由模板数量有限，信号量字典不会随参数值增长。
var apiConcurrencyGate = new SemaphoreSlim(maxConcurrentApiRequests, maxConcurrentApiRequests);
var routeConcurrencyGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
app.UseExceptionHandler();
// 普通 API 在服务端也设置硬超时：即使客户端已经放弃等待，数据库/计算任务也会收到取消信号，
// 防止半断开请求继续占用线程、数据库连接和量化并发槽。SSE 是有意保持的长连接，因此明确豁免。
app.Use(async (context, next) =>
{
    var timeout = ResolveRequestTimeout(
        context.Request.Path,
        defaultRequestTimeout,
        heartbeatRequestTimeout,
        exportRequestTimeout,
        quantizeRequestTimeout);
    if (timeout is null)
    {
        await next();
        return;
    }

    var clientAbort = context.RequestAborted;
    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(clientAbort);
    timeoutSource.CancelAfter(timeout.Value);
    context.RequestAborted = timeoutSource.Token;
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !clientAbort.IsCancellationRequested)
    {
        context.RequestAborted = clientAbort;
        app.Logger.LogWarning("请求处理超时：{Method} {Path}，上限 {TimeoutSeconds}s。", context.Request.Method, context.Request.Path, timeout.Value.TotalSeconds);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsJsonAsync(new { message = "服务器处理超时，请稍后重试。", code = "SERVER_TIMEOUT" }, clientAbort);
        }
    }
    catch (OperationCanceledException) when (clientAbort.IsCancellationRequested)
    {
        // 浏览器主动离开或取消请求属于正常情况，停止后端工作且不记录为 500。
    }
    catch (MySqlException exception) when (!context.Response.HasStarted)
    {
        context.RequestAborted = clientAbort;
        app.Logger.LogWarning(exception, "数据库请求失败：{Method} {Path}。", context.Request.Method, context.Request.Path);
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { message = "数据库暂时不可用，请稍后重试。", code = "DATABASE_UNAVAILABLE" }, clientAbort);
    }
    catch (TimeoutException exception) when (!context.Response.HasStarted)
    {
        context.RequestAborted = clientAbort;
        app.Logger.LogWarning(exception, "依赖服务超时：{Method} {Path}。", context.Request.Method, context.Request.Path);
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { message = "依赖服务响应超时，请稍后重试。", code = "DEPENDENCY_TIMEOUT" }, clientAbort);
    }
    finally
    {
        context.RequestAborted = clientAbort;
    }
});
// 客户端中断上传或提交损坏 JSON 时属于可恢复的请求错误，不应升级成 500 并刷满异常日志。
// 统一转换为 400，既保护后端请求管线，也让前端能够立即结束等待状态并给出重试提示。
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (JsonException) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "请求数据格式不正确。" });
    }
    catch (BadHttpRequestException) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "请求内容无效或已中断。" });
    }
});
// 安全响应头：防 MIME 嗅探、点击劫持、跨站引用泄露；API 响应禁止缓存敏感数据。
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-XSS-Protection"] = "1; mode=block";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (context.Request.Path.StartsWithSegments("/api"))
        headers["Cache-Control"] = "no-store";
    await next();
});
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseCors();
// 显式启用路由：EndpointRoutingMiddleware 必须先在 pipeline 中运行并设置 HttpContext 端点，
// RateLimitingMiddleware 才能读取端点上的 RequireRateLimiting 元数据；否则所有接口限流静默失效。
app.UseRouting();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isSse = path.StartsWithSegments("/api/license/events")
        || path.StartsWithSegments("/api/license/switches/events")
        || path.StartsWithSegments("/api/license/states/events")
        || path.StartsWithSegments("/api/collab/events");
    if (!path.StartsWithSegments("/api") || isSse)
    {
        await next();
        return;
    }

    if (!apiConcurrencyGate.Wait(0))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { message = "服务器当前请求较多，请稍后重试。", code = "API_BUSY" }, context.RequestAborted);
        return;
    }

    var routeKey = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
        ?? context.Request.Path.Value
        ?? "unknown";
    var routeGate = routeConcurrencyGates.GetOrAdd(
        routeKey,
        _ => new SemaphoreSlim(maxConcurrentRequestsPerRoute, maxConcurrentRequestsPerRoute));
    if (!routeGate.Wait(0))
    {
        apiConcurrencyGate.Release();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { message = "该功能当前使用人数较多，请稍后重试。", code = "ROUTE_BUSY" }, context.RequestAborted);
        return;
    }

    try
    {
        await next();
    }
    finally
    {
        routeGate.Release();
        apiConcurrencyGate.Release();
    }
});
app.UseRateLimiter();
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

// 图片量化全局并发信号量：默认最多两个重计算任务，给心跳、存档和 SSE 留出 CPU/内存余量。
var quantizeGate = new SemaphoreSlim(quantizeMaxConcurrency, quantizeMaxConcurrency);
// 三个 SSE 长连接端点的并发连接计数（switches/states/license），防止连接及内存耗尽。
var sseConnectionCounts = new int[3];
const int MaxSseConnectionsPerEndpoint = 100;
// 安全策略：Security:RejectDefaultSecrets=true 时，检测到默认密钥直接拒绝启动，防止生产环境带默认凭据上线。
// 校验的是「最终实际生效的管理密钥」（AdminTokenStore.Token），避免持久化文件中的旧默认值覆盖新配置。
var rejectDefaultSecrets = app.Configuration.GetValue("Security:RejectDefaultSecrets", false);
var adminTokenStore = app.Services.GetRequiredService<AdminTokenStore>();
var configuredAdminToken = app.Configuration["License:AdminToken"] ?? "";
var hasDefaultSecret = string.IsNullOrWhiteSpace(configuredAdminToken)
    || configuredAdminToken.Equals("change-this-admin-token", StringComparison.OrdinalIgnoreCase)
    || configuredAdminToken.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase);
if (hasDefaultSecret && rejectDefaultSecrets)
    throw new InvalidOperationException("检测到默认管理密钥（实际生效），已按配置拒绝启动。请更换为随机值并清理旧 admin-token.txt 后再运行。");
if (hasDefaultSecret)
    app.Logger.LogWarning("检测到默认管理密钥（实际生效），生产环境请更换为随机值并开启 Security:RejectDefaultSecrets。");

// 匿名会话中间件：为每个访问者自动签发 24 小时 HttpOnly 会话 Cookie，提供请求身份
// （阶段2/3：替代前端 HMAC 签名，防 CSRF/跨站裸调）；会话不阻断访问（匿名放行），
// 业务鉴权仍由各接口自身的密钥/会话/管理 Cookie 承担。
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (request.Path.StartsWithSegments("/api"))
    {
        var sessionStore = context.RequestServices.GetRequiredService<AnonymousSessionStore>();
        var session = request.Cookies["pindou_session"];
        if (string.IsNullOrEmpty(session) || !sessionStore.IsValid(session))
        {
            try
            {
                var newSession = sessionStore.Create();
                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = AnonymousSessionStore.Lifetime
                };
                context.Response.Cookies.Append("pindou_session", newSession, cookieOptions);
            }
            catch (InvalidOperationException)
            {
                // 会话容量满时匿名继续，不影响访问。
            }
        }
    }
    await next();
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "拼了个豆 API",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/catalog/brands", (PaletteCatalog catalog) => Results.Ok(catalog.GetBrands()));

app.MapGet("/api/catalog/brands/{brandId}/palettes/{paletteId}",
    (string brandId, string paletteId, PaletteCatalog catalog) =>
        catalog.GetPalette(brandId, paletteId) is { } palette
            ? Results.Ok(palette)
            : Results.NotFound(new { message = "未找到对应品牌色卡。" }));

var boardPresets = new BoardPreset[]
{
    new("mini-52", "迷你豆整板 52×52", 52, 52, 2.6, "迷你豆", "适合2.6mm豆，一块可覆盖52×52图纸。"),
    new("mini-26", "迷你豆半板 26×26", 26, 26, 2.6, "迷你豆", "适合小挂件和局部拼接。"),
    new("midi-29", "标准豆拼接板 29×29", 29, 29, 5.0, "标准豆", "最常见5mm透明拼接板。"),
    new("midi-15", "标准豆小板 15×15", 15, 15, 5.0, "标准豆", "适合16×16附近的小图。"),
    new("maxi-12", "大颗粒板 12×12", 12, 12, 10.0, "大颗粒", "适合儿童10mm大豆。")
};

app.MapGet("/api/catalog/boards", () => Results.Ok(boardPresets));

// 目录批量接口：一次返回厂商色卡与底板预设，减少前端初始化请求次数。
app.MapGet("/api/catalog/overview", (PaletteCatalog catalog) => Results.Ok(new
{
    brands = catalog.GetBrands(),
    boards = boardPresets
}));

app.MapPost("/api/handoffs", (BrowserHandoffRequest request, BrowserHandoffStore handoffs, HttpContext context) =>
{
    try
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        return Results.Ok(handoffs.Create(request.Project, clientIp));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        // 接力存储达到容量上限：稳定返回 503，不再分配内存。
        return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("api");

app.MapGet("/api/handoffs/{token}", (string token, BrowserHandoffStore handoffs, HttpResponse response) =>
{
    var projectJson = handoffs.Take(token);
    if (projectJson is null) return Results.NotFound(new { message = "图纸接力链接已失效或已经使用。" });
    response.Headers.CacheControl = "no-store";
    return Results.Content(projectJson, "application/json");
});

static bool IsAdminAuthorized(HttpRequest request, AdminTokenStore adminToken, AdminSessionStore adminSessions)
{
    // HttpOnly Cookie 会话优先；兼容 X-Admin-Token（固定管理密钥）便于脚本/工具调用。
    var session = request.Cookies["pindou_admin"];
    if (!string.IsNullOrEmpty(session) && adminSessions.IsValid(session)) return true;
    var expected = adminToken.Token;
    var provided = request.Headers["X-Admin-Token"].ToString();
    return expected.Length > 0 &&
        expected.Length == provided.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(provided));
}

app.MapGet("/api/license/info", async (HttpRequest request, LicenseStore licenses, CancellationToken ct) =>
{
    var status = await licenses.InspectAsync(request.Headers["X-License-Key"].ToString(), ct);
    return Results.Ok(new { status.Status, status.RemainingSeconds, status.RemainingCount, status.Message });
});

// 页面加载时查询授权状态：有密钥返回密钥校验结果，无密钥返回当前 IP 的试用剩余时间与次数；授权开关关闭时返回免授权。
app.MapGet("/api/license/trial", async (HttpRequest request, HttpContext context, LicenseStore licenses, IpTrialTracker ipTrials, FeatureSwitchStore switches, CancellationToken ct) =>
{
    var enableAuth = switches.EnableAuth;
    var licenseKey = request.Headers["X-License-Key"].ToString();
    if (enableAuth && !string.IsNullOrWhiteSpace(licenseKey))
    {
        var license = await licenses.InspectAsync(licenseKey, ct);
        return Results.Ok(new { licensed = true, license = new { license.Status, license.RemainingSeconds, license.RemainingCount, license.Message } });
    }
    // 授权开关关闭时返回未启用，前端 trialResolved=false 保持宽松放行。
    if (!enableAuth)
        return Results.Ok(new { licensed = false, licensingDisabled = true, trial = (Pindou.Api.Models.TrialStatus?)null });
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var trial = await ipTrials.GetTrialStatusAsync(clientIp, ct);
    return Results.Ok(new { licensed = false, licensingDisabled = false, trial });
});

// 试用心跳：试用状态仅在前台在线时累加时长；关闭/后台/锁屏/断网无心跳则不累计。
app.MapGet("/api/license/trial/heartbeat", async (HttpContext context, IpTrialTracker ipTrials, CancellationToken ct) =>
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var trial = await ipTrials.HeartbeatAsync(clientIp, ct);
    return Results.Ok(trial);
});

// 登录：按 IP 恢复账号，激活密钥并绑定账号，返回新用户/老用户与云端存档信息。
app.MapPost("/api/license/login", async (HttpRequest request, HttpContext context, LicenseStore licenses, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<LicenseActionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { message = "密钥不能为空。" });
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var result = await licenses.LoginAsync(body.Key, clientIp, body.SessionToken, ct);
    return Results.Ok(result);
}).RequireRateLimiting("login");

// 按设备账号读取云端画布存档；无存档时返回 404。
app.MapGet("/api/saves", async (HttpRequest request, CanvasSaveStore saves, UserStore users, CancellationToken ct) =>
{
    var key = request.Headers["X-License-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { message = "缺少密钥。" });
    if (!await users.IsActiveSessionAsync(key, request.Headers["X-Session-Token"].ToString(), ct))
        return Results.Json(new { message = "该账号已在其他设备登录，本设备已下线。", code = "DEVICE_CONFLICT" }, statusCode: StatusCodes.Status409Conflict);
    var payload = await saves.GetAsync(key, ct);
    return payload is null ? Results.NotFound(new { message = "暂无存档。" }) : Results.Ok(new { payload });
});

// 按设备账号写入云端画布存档。
app.MapPost("/api/saves", async (HttpRequest request, CanvasSaveStore saves, UserStore users, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<SaveRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Key) || string.IsNullOrWhiteSpace(body.Payload))
        return Results.BadRequest(new { message = "存档数据不完整。" });
    // 业务级大小限制，防止持有有效会话的客户端持续提交大数据。
    if (System.Text.Encoding.UTF8.GetByteCount(body.Payload) > 2 * 1024 * 1024)
        return Results.BadRequest(new { message = "存档数据过大，无法保存。" });
    if (!await users.IsActiveSessionAsync(body.Key, request.Headers["X-Session-Token"].ToString(), ct))
        return Results.Json(new { message = "该账号已在其他设备登录，本设备已下线。", code = "DEVICE_CONFLICT" }, statusCode: StatusCodes.Status409Conflict);
    await saves.SaveAsync(body.Key, body.Payload, ct);
    return Results.Ok(new { message = "已保存" });
}).RequireRateLimiting("save");

// 会话开始：重置计时起点，确保关闭浏览器期间的时长不被累加。
app.MapPost("/api/license/session", async (HttpRequest request, LicenseStore licenses, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<LicenseActionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { message = "密钥不能为空。" });
    var status = await licenses.BeginSessionAsync(body.Key, body.SessionToken, ct);
    return Results.Ok(new { status.Status, status.RemainingSeconds, status.RemainingCount, status.Message, status.KickInSeconds });
});

// 心跳：累加在线时长并刷新计时起点。
app.MapPost("/api/license/heartbeat", async (HttpRequest request, LicenseStore licenses, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<LicenseActionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { message = "密钥不能为空。" });
    var status = await licenses.HeartbeatAsync(body.Key, body.SessionToken, ct);
    return Results.Ok(new { status.Status, status.RemainingSeconds, status.RemainingCount, status.Message, status.KickInSeconds });
});

// 登出（退出登录）：清除会话并标记密钥离线。
app.MapPost("/api/license/logout", async (HttpRequest request, LicenseStore licenses, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<LicenseActionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Key))
        return Results.BadRequest(new { message = "密钥不能为空。" });
    // 登出必须匹配当前会话令牌：令牌不匹配说明会话已失效或被其他设备替换，拒绝登出。
    var cleared = await licenses.LogoutAsync(body.Key, body.SessionToken, ct);
    if (!cleared)
        return Results.Json(new { message = "会话已失效或账号已在其他设备登录，本设备已下线。", code = "DEVICE_CONFLICT" }, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { message = "已退出" });
});

// SSE 长连接：设备订阅会话接管事件；新设备登录时服务端即时通知旧设备下线。
app.MapGet("/api/license/events", async (HttpRequest request, HttpResponse response, SseTicketStore tickets, SessionEventHub events, CancellationToken ct) =>
{
    // 从一次性票据恢复会话令牌，避免把授权密钥/会话令牌放入 URL。
    var ticket = request.Query["ticket"].ToString();
    var (ok, sessionToken) = tickets.Consume(ticket, "kick");
    if (!ok || string.IsNullOrEmpty(sessionToken))
        return Results.Unauthorized();

    // 限制单端点 SSE 并发连接数，防止连接及内存耗尽。
    if (Interlocked.Increment(ref sseConnectionCounts[2]) > MaxSseConnectionsPerEndpoint)
    {
        Interlocked.Decrement(ref sseConnectionCounts[2]);
        return Results.Json(new { message = "实时连接数已达上限，请稍后重试。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    ConfigureSseResponse(response);
    var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
    events.Subscribe(sessionToken, channel);
    try
    {
        await response.WriteAsync(": connected\n\n", ct);
        await response.Body.FlushAsync(ct);
        await PumpSseAsync(response, channel.Reader, sseKeepaliveInterval, ct);
    }
    catch (OperationCanceledException)
    {
        // 客户端断开连接。
    }
    finally
    {
        events.Unsubscribe(sessionToken, channel);
        Interlocked.Decrement(ref sseConnectionCounts[2]);
    }
    return Results.Empty;
});

// 批量生成密钥：Number 为一次生成的密钥数量（1–100，默认 1），单条多值 INSERT 写入，返回全部新密钥。
app.MapPost("/api/admin/licenses", async (HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var body = await request.ReadFromJsonAsync<CreateLicenseRequest>(cancellationToken: ct);
    if (body is null || body.Count <= 0)
        return Results.BadRequest(new { message = "生成次数必须是正整数。" });
    // 期限优先取小时（totalHours，自动转为秒），否则兼容秒（totalSeconds）。
    // totalHours / totalSeconds 为 0 时表示无期限密钥（永久有效），后端校验跳过时长耗尽判断。
    var totalSeconds = body.TotalHours is > 0 ? body.TotalHours.Value * 3600
        : body.TotalHours == 0 ? 0
        : body.TotalSeconds;
    if (totalSeconds is null)
        return Results.BadRequest(new { message = "请提供有效期限（小时或秒）和生成次数。" });
    var number = body.Number is > 0 and <= 100 ? body.Number.Value : 1;
    var keys = await licenses.CreateManyAsync(totalSeconds.Value, body.Count, number, ct);
    return Results.Ok(new { keys });
}).RequireRateLimiting("api");

// 批量吊销密钥：逻辑失效（记录保留），之后任何校验返回 revoked；单次最多 100 个。
app.MapPost("/api/admin/licenses/revoke", async (HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var body = await request.ReadFromJsonAsync<RevokeLicenseRequest>(cancellationToken: ct);
    if (body is null || body.Keys is null || body.Keys.Count == 0)
        return Results.BadRequest(new { message = "请提供要吊销的密钥列表。" });
    if (body.Keys.Count > 100)
        return Results.BadRequest(new { message = "单次吊销最多 100 个密钥。" });
    var revoked = await licenses.RevokeManyAsync(body.Keys, ct);
    return Results.Ok(new { revoked });
}).RequireRateLimiting("api");

// 批量删除密钥：物理移除记录；单次最多 100 个。
app.MapPost("/api/admin/licenses/delete", async (HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var body = await request.ReadFromJsonAsync<RevokeLicenseRequest>(cancellationToken: ct);
    if (body is null || body.Keys is null || body.Keys.Count == 0)
        return Results.BadRequest(new { message = "请提供要删除的密钥列表。" });
    if (body.Keys.Count > 100)
        return Results.BadRequest(new { message = "单次删除最多 100 个密钥。" });
    var deleted = await licenses.DeleteManyAsync(body.Keys, ct);
    return Results.Ok(new { deleted });
}).RequireRateLimiting("api");

// 分页查询密钥列表：page 从 1 起，pageSize 默认 20、上限 100。
app.MapGet("/api/admin/licenses", async (HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var page = int.TryParse(request.Query["page"].ToString(), out var p) && p >= 1 ? p : 1;
    var pageSize = int.TryParse(request.Query["pageSize"].ToString(), out var ps) && ps is >= 1 and <= 100 ? ps : 20;
    var status = request.Query["status"].ToString();
    var (items, total) = await licenses.ListPagedAsync(page, pageSize, status, ct);
    return Results.Ok(new { items, total, page, pageSize });
}).RequireRateLimiting("api");

// 密钥使用详情：含上次登录 IP、最后活跃时间与在线状态。
app.MapGet("/api/admin/licenses/{key}/detail", async (string key, HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var detail = await licenses.GetDetailAsync(key, ct);
    return detail is null ? Results.NotFound(new { message = "未找到该密钥。" }) : Results.Ok(detail);
}).RequireRateLimiting("api");

// 删除单个密钥（物理移除）；兼容单个删除场景。
app.MapDelete("/api/admin/licenses/{key}", async (string key, HttpRequest request, LicenseStore licenses, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var removed = await licenses.DeleteAsync(key, ct);
    return removed ? Results.Ok(new { message = "密钥已删除。" }) : Results.NotFound(new { message = "未找到该密钥。" });
}).RequireRateLimiting("api");

// 运行时特性开关：查询当前授权（含试用）是否启用（无需鉴权即可读状态）。
app.MapGet("/api/admin/switches", (FeatureSwitchStore switches) => Results.Ok(switches.Snapshot()));

// 管理登录：提交管理密钥，校验通过后签发 HttpOnly 会话 Cookie（替代前端 localStorage 存长期令牌）。
app.MapPost("/api/admin/login", async (HttpRequest request, HttpResponse response, AdminTokenStore adminToken, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<AdminLoginRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { message = "请提供管理密钥。" });
    var provided = body.Token.Trim();
    var expected = adminToken.Token;
    if (expected.Length == 0 || expected.Length != provided.Length ||
        !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(provided)))
        return Results.Unauthorized();
    string session;
    try
    {
        session = adminSessions.Create();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var cookieOptions = new CookieOptions
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromHours(8)
    };
    response.Cookies.Append("pindou_admin", session, cookieOptions);
    return Results.Ok(new { message = "登录成功" });
}).RequireRateLimiting("login");

// 管理登出：撤销服务端会话并清除 Cookie。
app.MapPost("/api/admin/logout", (HttpRequest request, HttpResponse response, AdminSessionStore adminSessions) =>
{
    var session = request.Cookies["pindou_admin"];
    adminSessions.Revoke(session ?? "");
    response.Cookies.Delete("pindou_admin");
    return Results.Ok(new { message = "已退出" });
});

// 管理会话检查：admin.html 据此判断当前是否已登录（Cookie 会话是否有效）。
app.MapGet("/api/admin/session", (HttpRequest request, AdminSessionStore adminSessions) =>
{
    var session = request.Cookies["pindou_admin"];
    return Results.Ok(new { authenticated = !string.IsNullOrEmpty(session) && adminSessions.IsValid(session) });
});

// 运行时特性开关：修改授权开关，无需重启立即生效（需管理员令牌），并向所有在线前端推送开关变化。
app.MapPut("/api/admin/switches", async (HttpRequest request, FeatureSwitchStore switches, SwitchEventHub switchEvents, AdminTokenStore adminTokens, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    if (!IsAdminAuthorized(request, adminTokens, adminSessions)) return Results.Unauthorized();
    var body = await request.ReadFromJsonAsync<FeatureSwitchRequest>(cancellationToken: ct);
    if (body is null) return Results.BadRequest(new { message = "请求体格式不正确。" });
    // 发布订阅：仅当授权状态真正变化时才推送，避免无效广播。
    var before = switches.EnableAuth;
    switches.Apply(body.EnableAuth);
    if (switches.EnableAuth != before)
        await switchEvents.BroadcastAsync(switches.EnableAuth, ct);
    return Results.Ok(switches.Snapshot());
}).RequireRateLimiting("api");

// 开关状态 SSE 长连接：开关被管理员接口修改时服务端即时推送 switch-changed 事件，前端无需轮询即可更新。
app.MapGet("/api/license/switches/events", async (HttpRequest request, HttpResponse response, FeatureSwitchStore switches, SwitchEventHub switchEvents, CancellationToken ct) =>
{
    // 限制单端点 SSE 并发连接数，防止连接及内存耗尽。
    if (Interlocked.Increment(ref sseConnectionCounts[0]) > MaxSseConnectionsPerEndpoint)
    {
        Interlocked.Decrement(ref sseConnectionCounts[0]);
        return Results.Json(new { message = "实时连接数已达上限，请稍后重试。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    ConfigureSseResponse(response);
    var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
    var id = switchEvents.Subscribe(channel);
    try
    {
        // 建立连接后立即推送一次当前开关状态，前端据此完成初始同步。
        var initPayload = "{\"enableAuth\":" + switches.EnableAuth.ToString().ToLowerInvariant() + "}";
        await response.WriteAsync("event: switch-changed\ndata: " + initPayload + "\n\n", ct);
        await response.Body.FlushAsync(ct);
        await PumpSseAsync(response, channel.Reader, sseKeepaliveInterval, ct);
    }
    catch (OperationCanceledException)
    {
        // 客户端断开连接。
    }
    finally
    {
        switchEvents.Unsubscribe(id);
        Interlocked.Decrement(ref sseConnectionCounts[0]);
    }
    return Results.Empty;
});

// 申请 SSE 连接票据（一次性、60 秒有效），避免把授权密钥/会话令牌/管理令牌放入 URL。
// kick：需用户会话；states：需管理鉴权；switches：公开。
app.MapPost("/api/license/tickets", async (HttpRequest request, SseTicketStore tickets, UserStore users, AdminTokenStore adminToken, AdminSessionStore adminSessions, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<SseTicketRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Purpose))
        return Results.BadRequest(new { message = "缺少票据用途。" });
    string? extra = null;
    switch (body.Purpose)
    {
        case "kick":
            var key = request.Headers["X-License-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
                return Results.BadRequest(new { message = "缺少密钥。" });
            if (!await users.IsActiveSessionAsync(key, request.Headers["X-Session-Token"].ToString(), ct))
                return Results.Unauthorized();
            extra = request.Headers["X-Session-Token"].ToString();
            break;
        case "states":
            if (!IsAdminAuthorized(request, adminToken, adminSessions))
                return Results.Unauthorized();
            break;
        case "switches":
            break;
        default:
            return Results.BadRequest(new { message = "未知的票据用途。" });
    }
    string ticket;
    try
    {
        ticket = tickets.Create(body.Purpose, extra);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new { ticket, expiresIn = 60 });
}).RequireRateLimiting("api");

// 密钥状态 SSE 长连接：仅后台管理页订阅（凭一次性票据），密钥上线/下线/停用/删除/心跳变化时
// 服务端即时推送 licenses-changed 事件，后台管理页收到后批量刷新当前页状态，无需手动刷新。
app.MapGet("/api/license/states/events", async (HttpRequest request, HttpResponse response, KeyStateEventHub keyEvents, SseTicketStore tickets, CancellationToken ct) =>
{
    // 从一次性票据校验管理身份，避免把管理令牌放入 URL。
    var ticket = request.Query["ticket"].ToString();
    var (ok, _) = tickets.Consume(ticket, "states");
    if (!ok)
        return Results.Unauthorized();

    // 限制单端点 SSE 并发连接数，防止连接及内存耗尽。
    if (Interlocked.Increment(ref sseConnectionCounts[1]) > MaxSseConnectionsPerEndpoint)
    {
        Interlocked.Decrement(ref sseConnectionCounts[1]);
        return Results.Json(new { message = "实时连接数已达上限，请稍后重试。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    ConfigureSseResponse(response);
    var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
    var id = keyEvents.Subscribe(channel);
    try
    {
        // 建立连接后立即推送一次，后台管理页据此完成初始同步。
        await response.WriteAsync("event: licenses-changed\ndata: {\"changed\":true}\n\n", ct);
        await response.Body.FlushAsync(ct);
        // 每 5 秒无条件推送一次「密钥状态已变化」信号（后台管理页据此批量刷新当前页），
        // 同时立即冲刷登录/登出/停用/删除等即时广播事件。
        var lastPush = DateTime.UtcNow;
        var pushInterval = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            while (channel.Reader.TryRead(out var message))
            {
                await response.WriteAsync(message, ct);
                await response.Body.FlushAsync(ct);
            }
            if (DateTime.UtcNow - lastPush >= pushInterval)
            {
                lastPush = DateTime.UtcNow;
                await response.WriteAsync("event: licenses-changed\ndata: {\"changed\":true}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            if (ct.IsCancellationRequested) break;
            var elapsed = DateTime.UtcNow - lastPush;
            var delay = pushInterval - elapsed;
            var waitTask = channel.Reader.WaitToReadAsync(ct).AsTask();
            var delayTask = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct);
            await Task.WhenAny(waitTask, delayTask);
        }
    }
    catch (OperationCanceledException)
    {
        // 客户端断开连接。
    }
    finally
    {
        keyEvents.Unsubscribe(id);
        Interlocked.Decrement(ref sseConnectionCounts[1]);
    }
    return Results.Empty;
});

app.MapPost("/api/patterns/quantize", async (
    HttpRequest request,
    HttpContext context,
    PaletteCatalog catalog,
    PatternQuantizer quantizer,
    PatternAggregateQuantizer aggregateQuantizer,
    LicenseStore licenses,
    IpTrialTracker ipTrials,
    FeatureSwitchStore switches,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "请使用multipart/form-data上传图片。" });

    var form = await request.ReadFormAsync(cancellationToken);
    var image = form.Files.GetFile("image");
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { message = "请选择图片。" });
    if (image.Length > 15 * 1024 * 1024)
        return Results.BadRequest(new { message = "图片不能超过15MB。" });

    var paletteId = form["paletteId"].ToString();
    var palette = catalog.Find(paletteId);
    if (palette is null)
        return Results.BadRequest(new { message = "色卡不存在。" });

    if (!int.TryParse(form["width"].ToString(), out var width) || width is < 8 or > 160 ||
        !int.TryParse(form["height"].ToString(), out var height) || height is < 8 or > 160)
        return Results.BadRequest(new { message = "图纸横向和纵向颗数必须是 8–160 的整数。" });

    var colorLimit = Math.Min(96, Math.Min(palette.Colors.Count, width * height));
    if (!int.TryParse(form["maxColors"].ToString(), out var maxColors) || maxColors < 2 || maxColors > colorLimit)
        return Results.BadRequest(new { message = $"最多颜色必须是 2–{colorLimit} 的整数，且不能超过图纸总格数。" });
    if (!bool.TryParse(form["dither"].ToString(), out var dither) ||
        !bool.TryParse(form["removeBackground"].ToString(), out var removeBackground))
        return Results.BadRequest(new { message = "图像处理开关参数不正确。" });
    if (!double.TryParse(form["backgroundThreshold"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) || threshold is < 2 or > 30)
        return Results.BadRequest(new { message = "背景容差必须在 2–30 之间。" });
    if (!int.TryParse(form["noiseSuppression"].ToString(), out var noiseSuppression) || noiseSuppression is < 0 or > 3)
        return Results.BadRequest(new { message = "杂色抑制强度必须是 0–3 的整数。" });

    // 全局图片量化并发信号量：先占额度再扣授权次数，避免排队时重复扣减；同时防止并发解码耗尽内存。
    if (!await quantizeGate.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken))
        return Results.Json(new { message = "服务器繁忙，请稍后重试。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    try
    {
        // 授权（含试用）：有密钥则校验并扣减一次；无密钥则按 IP 试用窗口与次数放行。
        // 开关配置（License:EnableAuth）可整体关闭校验，直接放行生成。
        var licenseKey = request.Headers["X-License-Key"].ToString();
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var enableAuth = switches.EnableAuth;

        if (enableAuth && !string.IsNullOrWhiteSpace(licenseKey))
        {
            var license = await licenses.ConsumeAsync(licenseKey, request.Headers["X-Session-Token"].ToString(), cancellationToken);
            if (license.Status != "active" && license.Status != "time_expired")
                return license.Status == "device_conflict"
                    ? Results.Json(new { message = license.Message, code = "DEVICE_CONFLICT" }, statusCode: StatusCodes.Status409Conflict)
                    : Results.Json(new { message = license.Message ?? "密钥不可用，请获取密钥。", code = "LICENSE_REQUIRED" }, statusCode: StatusCodes.Status402PaymentRequired);
        }
        else if (enableAuth && string.IsNullOrWhiteSpace(licenseKey))
        {
            // 原子占用一次试用生成（时间与次数同条件校验），避免并发请求超发免费次数。
            var consume = await ipTrials.TryConsumeGenerationAsync(clientIp, cancellationToken);
            if (!consume.Allowed)
                return Results.Json(new { message = consume.Reason, code = "LICENSE_REQUIRED" }, statusCode: StatusCodes.Status402PaymentRequired);
        }

        await using var stream = image.OpenReadStream();
        var algorithm = form["algorithm"].ToString();
        // 像素聚合(pixel-aggregate)为默认；CIEDE2000 路径保留用于兼容既有业务。
        var result = string.Equals(algorithm, "ciede2000", StringComparison.OrdinalIgnoreCase)
            ? quantizer.Quantize(stream, palette, width, height, maxColors, dither, removeBackground, threshold, noiseSuppression, cancellationToken)
            : aggregateQuantizer.Quantize(stream, width, height, maxColors, removeBackground, threshold, palette, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    finally
    {
        quantizeGate.Release();
    }
}).RequireRateLimiting("api");

app.MapPost("/api/exports/xlsx", async (HttpRequest request, ExcelExportService exporter, CancellationToken ct) =>
{
    // 阶段1补强：该接口面向公开（试用/访客也可导出），故在资源层做防护——
    // 限制请求体大小，防止超大 cells 数组消耗 CPU/内存；配合限流与签名防裸调。
    request.EnableBuffering();
    using var bodyReader = new StreamReader(request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
    var bodyText = await bodyReader.ReadToEndAsync(ct);
    if (System.Text.Encoding.UTF8.GetByteCount(bodyText) > 1 * 1024 * 1024)
        return Results.BadRequest(new { message = "导出数据过大。" });
    PatternExportRequest? parsed;
    try
    {
        // 读回起点后用 ASP.NET Core 的 ReadFromJsonAsync（属性名大小写不敏感，与前端小写 JSON 兼容）。
        request.Body.Position = 0;
        parsed = await request.ReadFromJsonAsync<PatternExportRequest>(cancellationToken: ct);
    }
    catch
    {
        return Results.BadRequest(new { message = "请求体格式不正确。" });
    }
    if (parsed is null)
        return Results.BadRequest(new { message = "请求体格式不正确。" });

    if (parsed.Width is < 1 or > 160 || parsed.Height is < 1 or > 160 || parsed.Cells.Count != parsed.Width * parsed.Height)
        return Results.BadRequest(new { message = "图纸数据尺寸不正确。" });
    if (parsed.Colors.Count == 0 || parsed.Cells.Any(i => i >= parsed.Colors.Count))
        return Results.BadRequest(new { message = "色卡数据不完整。" });

    var bytes = exporter.Create(parsed, ct);
    var safeTitle = string.Concat((parsed.Title ?? "拼豆图纸").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
    if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "拼豆图纸";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{safeTitle}.xlsx");
}).RequireRateLimiting("api");

// ---------- 好友联机（Collab） ----------
// 联机成员身份凭证（memberToken）只通过 HTTPS 请求体传递，SSE 使用一次性票据连接，避免令牌入 URL。
var collabSseConnectionCount = 0;
const int MaxCollabSseConnections = 200;

// 房主发起联机：携带当前豆板快照创建房间；授权开启时房主必须持有有效授权密钥。
app.MapPost("/api/collab/host", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabHostRequest>(cancellationToken: ct);
    if (body is null || body.Snapshot is null)
        return Results.BadRequest(new { message = "请求数据不完整。" });
    var licenseKey = request.Headers["X-License-Key"].ToString();
    var hostError = await collab.ValidateHostAsync(licenseKey, ct);
    if (hostError is not null)
        return Results.Json(new { message = hostError }, statusCode: StatusCodes.Status402PaymentRequired);
    CollabRoom room;
    CollabMember host;
    try
    {
        (room, host) = collab.HostCreate(body.Snapshot, licenseKey);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    return Results.Ok(new
    {
        roomId = room.RoomId,
        inviteCode = room.InviteCode,
        hostToken = host.Token,
        memberId = host.MemberId,
        hostName = host.Name,
        colorIndex = host.ColorIndex,
        seq = room.EditSeq,
        boardRevision = room.BoardRevision,
        room = CollabService.BuildRoomDto(room),
    });
}).RequireRateLimiting("api");

// 房主刷新邀请码：旧码与旧邀请链接作废，已联机好友不受影响（不踢出），仅作废待审批申请。
app.MapPost("/api/collab/refresh", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabTokenRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { message = "缺少联机令牌。" });
    try
    {
        var inviteCode = collab.RefreshInvite(body.Token);
        return Results.Ok(new { inviteCode });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 好友凭邀请码申请加入：返回 applyId，房主审批后轮询 apply/result 获取结果。
// 授权开启时校验申请人资格：试用到期（无有效密钥）不可申请联机（复制链接/手动输入邀请码同走此入口）。
app.MapPost("/api/collab/apply", async (HttpRequest request, HttpContext context, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabApplyRequest>(cancellationToken: ct);
    if (body is null)
        return Results.BadRequest(new { message = "请求数据不完整。" });
    var licenseKey = request.Headers["X-License-Key"].ToString();
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var result = await collab.ApplyAsync(body.InviteCode ?? "", licenseKey, clientIp, ct);
    if (!string.IsNullOrEmpty(result.Error))
        return Results.BadRequest(new { message = result.Error });
    return Results.Ok(new { applyId = result.ApplyId, name = "等待房主审批" });
}).RequireRateLimiting("api");

// 申请人主动取消尚未审批的联机申请；取消成功后服务端即时通知房主移除对应列表项。
app.MapPost("/api/collab/apply/cancel", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabCancelApplyRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.ApplyId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    var (ok, error) = collab.CancelApply(body.ApplyId);
    return ok
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { message = error ?? "联机申请已失效。" });
}).RequireRateLimiting("api");

// 房主审批好友申请（同意/拒绝）。
app.MapPost("/api/collab/decide", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabDecideRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.ApplyId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        var (ok, error) = collab.Decide(body.Token, body.ApplyId, body.Accept);
        if (!ok) return Results.BadRequest(new { message = error ?? "处理申请失败。" });
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 好友轮询审批结果：pending=true 表示仍在等待，approved 携带成员身份与房主豆板快照。
// 该接口含敏感数据（审批通过时的成员令牌），applyId 为 96 位随机凭证；叠加限流降低暴力枚举/滥用风险。
app.MapGet("/api/collab/apply/result/{applyId}", (string applyId, CollabService collab) =>
{
    var result = collab.GetApplyResult(applyId);
    return result is null ? Results.Ok(new { pending = true }) : Results.Content(result, "application/json");
}).RequireRateLimiting("api");

// 房主踢出联机成员。
app.MapPost("/api/collab/kick", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabKickRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.MemberId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        return collab.Kick(body.Token, body.MemberId)
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { message = "无法踢出该成员。" });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 房主单独开启/关闭某个成员的编辑权限。
app.MapPost("/api/collab/permission", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabPermissionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.MemberId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        return collab.SetPermission(body.Token, body.MemberId, body.CanEdit)
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { message = "无法修改该成员的权限。" });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 房主单独开启/关闭某个成员的保存共享权限。
app.MapPost("/api/collab/savepermission", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabSavePermissionRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.MemberId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        return collab.SetSavePermission(body.Token, body.MemberId, body.CanSave)
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { message = "无法修改该成员的共享权限。" });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 成员退出联机；房主退出会关闭整个房间并踢出全部好友。
app.MapPost("/api/collab/leave", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabLeaveRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { message = "缺少联机令牌。" });
    return collab.Leave(body.Token)
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { message = "联机状态已失效。" });
}).RequireRateLimiting("api");

// 成员提交编辑：服务端仲裁冲突并广播 edits/reverts/locks。
app.MapPost("/api/collab/edits", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabEditRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || body.Edits is null || body.Edits.Length == 0)
        return Results.BadRequest(new { message = "请求数据不完整。" });
    if (body.Edits.Length > 600)
        return Results.BadRequest(new { message = "单次提交的编辑数量过多。" });
    var (seq, edits, reverts, locks, canUndo, canRedo, error) = collab.SubmitEdits(body.Token, body.OperationId, body.BoardRevision, body.Edits);
    if (error is not null && edits.Length == 0 && reverts.Length == 0)
        return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { seq, edits, reverts, locks, canUndo, canRedo });
}).RequireRateLimiting("collab-edit");

// 服务端权威撤销/恢复：只处理当前成员自己的历史，并以格子版本阻止覆盖他人后续编辑。
app.MapPost("/api/collab/history", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabHistoryRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.Action))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    var (seq, edits, canUndo, canRedo, error) = collab.ApplyOwnHistory(body.Token, body.Action);
    if (error is not null && edits.Length == 0)
        return Results.Json(new { message = error, seq, canUndo, canRedo }, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { seq, edits, canUndo, canRedo });
}).RequireRateLimiting("collab-edit");

// 房主清空共享画布：清空房间全部格子并广播 clear 事件给所有成员（成员端整体清空本地画布）。
app.MapPost("/api/collab/clear", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabTokenRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { message = "缺少联机令牌。" });
    var error = collab.Clear(body.Token);
    if (error is not null)
        return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("api");

// 成员申请权限（编辑 edit / 共享 save）：仅可申请自己的权限；30 秒冷却、30 秒超时；广播 perm_apply 通知房主。
app.MapPost("/api/collab/permission-apply", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabPermApplyRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.Perm))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    var (applyId, error) = await collab.ApplyPermissionAsync(body.Token, body.Perm, ct);
    if (error is not null)
        return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { applyId });
}).RequireRateLimiting("api");

// 房主审批成员权限申请（同意/拒绝）：同意后设置对应权限并广播，结果通知申请成员。
app.MapPost("/api/collab/permission-decide", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabPermDecideRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.ApplyId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        var (ok, error) = collab.DecidePermission(body.Token, body.ApplyId, body.Accept);
        if (!ok)
            return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 房主生成/替换整块画布后同步给成员：更新房间权威快照并广播 resync 事件（联机豆板数据统一）。
app.MapPost("/api/collab/resync", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabResyncRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || body.Snapshot is null)
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        var error = collab.ResyncSnapshot(body.Token, body.Snapshot);
        if (error is not null)
            return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 成员申请替换整张联机图纸：1 分钟冷却与有效期，广播 replace_apply 通知房主；房主同意后全房间同步新图纸。
app.MapPost("/api/collab/replace-request", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabReplaceRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || body.Snapshot is null)
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        var (applyId, error) = await collab.ReplaceRequestAsync(body.Token, body.Snapshot, ct);
        if (error is not null)
            return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
        return Results.Ok(new { ok = true, applyId });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 房主审批成员替换图纸申请（同意/拒绝）：同意后整体替换房间快照并广播 resync，房主与所有成员同步更新后的图纸。
app.MapPost("/api/collab/replace-decide", async (HttpRequest request, CollabService collab, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabReplaceDecideRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.ApplyId))
        return Results.BadRequest(new { message = "请求数据不完整。" });
    try
    {
        var (ok, error) = collab.ReplaceDecide(body.Token, body.ApplyId, body.Accept);
        if (!ok)
            return Results.Json(new { message = error }, statusCode: StatusCodes.Status409Conflict);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
}).RequireRateLimiting("api");

// 申请联机 SSE 连接票据（一次性、60 秒有效）：用请求体携带成员令牌换取票据，避免令牌入 URL。
app.MapPost("/api/collab/ticket", async (HttpRequest request, CollabService collab, SseTicketStore tickets, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<CollabTokenRequest>(cancellationToken: ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Token))
        return Results.BadRequest(new { message = "缺少联机令牌。" });
    if (collab.GetRoomByToken(body.Token) is null)
        return Results.Json(new { message = "联机状态已失效。" }, statusCode: StatusCodes.Status409Conflict);
    string ticket;
    try
    {
        ticket = tickets.Create("collab", body.Token);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new { ticket, expiresIn = 60 });
}).RequireRateLimiting("api");

// 联机事件 SSE 长连接：房间状态、编辑同步、踢出/关闭、权限变更即时推送。
app.MapGet("/api/collab/events", async (HttpRequest request, HttpResponse response, CollabService collab, SseTicketStore tickets, CancellationToken ct) =>
{
    var ticket = request.Query["ticket"].ToString();
    var (ok, token) = tickets.Consume(ticket, "collab");
    if (!ok || string.IsNullOrEmpty(token))
        return Results.Unauthorized();
    if (Interlocked.Increment(ref collabSseConnectionCount) > MaxCollabSseConnections)
    {
        Interlocked.Decrement(ref collabSseConnectionCount);
        return Results.Json(new { message = "联机实时连接数已达上限，请稍后重试。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    var room = collab.Subscribe(token, channel, out var subscribedMemberId, out var subscribeError);
    if (room is null)
    {
        Interlocked.Decrement(ref collabSseConnectionCount);
        return Results.Json(new { message = subscribeError ?? "联机状态已失效。" }, statusCode: subscribeError is null ? StatusCodes.Status409Conflict : StatusCodes.Status429TooManyRequests);
    }
    ConfigureSseResponse(response);
    try
    {
        await response.WriteAsync(": connected\n\n", ct);
        // 初始状态（房间 + 快照 + 本人信息）：断线重连后据此恢复画布。
        var initial = collab.GetInitialStateJson(token);
        if (initial is not null)
        {
            await response.WriteAsync("event: state\ndata: " + initial + "\n\n", ct);
        }
        await response.Body.FlushAsync(ct);
        await PumpSseAsync(response, channel.Reader, sseKeepaliveInterval, ct);
    }
    catch (OperationCanceledException)
    {
        // 客户端断开连接。
    }
    finally
    {
        // 传入订阅者成员 id：房主最后一条连接断开时服务端据此判定离线。
        collab.Unsubscribe(room.RoomId, subscribedMemberId, channel);
        Interlocked.Decrement(ref collabSseConnectionCount);
    }
    return Results.Empty;
});

app.Run();

// 配置读取统一做边界收敛：生产环境误填极端数值时仍保持可预测行为。
static int ReadBoundedInt(IConfiguration configuration, string key, int fallback, int minimum, int maximum) =>
    Math.Clamp(configuration.GetValue(key, fallback), minimum, maximum);

// SSE 依靠应用层保活维持连接，不应套用普通 API 的硬超时。
static TimeSpan? ResolveRequestTimeout(
    PathString path,
    TimeSpan defaultTimeout,
    TimeSpan heartbeatTimeout,
    TimeSpan exportTimeout,
    TimeSpan quantizeTimeout)
{
    if (!path.StartsWithSegments("/api")) return null;
    if (path.StartsWithSegments("/api/license/events")
        || path.StartsWithSegments("/api/license/switches/events")
        || path.StartsWithSegments("/api/license/states/events")
        || path.StartsWithSegments("/api/collab/events"))
        return null;
    if (path.StartsWithSegments("/api/license/heartbeat")
        || path.StartsWithSegments("/api/license/trial/heartbeat"))
        return heartbeatTimeout;
    if (path.StartsWithSegments("/api/exports/xlsx")) return exportTimeout;
    if (path.StartsWithSegments("/api/patterns/quantize")) return quantizeTimeout;
    return defaultTimeout;
}

static void ConfigureSseResponse(HttpResponse response)
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache, no-transform";
    // 告诉 Nginx 等反向代理不要缓存/聚合事件，否则前端会误判连接断开并频繁重连。
    response.Headers["X-Accel-Buffering"] = "no";
}

// 无业务事件时定期发送 SSE 注释保活；每轮等待都有独立取消令牌，不遗留后台 WaitToReadAsync。
// 多条事件先批量写入再 Flush，降低高频协作编辑时的系统调用数量。
static async Task PumpSseAsync(HttpResponse response, ChannelReader<string> reader, TimeSpan keepaliveInterval, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var hasMessages = false;
        while (reader.TryRead(out var message))
        {
            hasMessages = true;
            await response.WriteAsync(message, ct);
        }
        if (hasMessages)
            await response.Body.FlushAsync(ct);

        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitSource.CancelAfter(keepaliveInterval);
        try
        {
            if (!await reader.WaitToReadAsync(waitSource.Token)) break;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await response.WriteAsync(": keepalive\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
