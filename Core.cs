using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ForumReviewMonitor;

public sealed class Settings
{
    public string ForumUrl { get; set; } = "https://forum.openeuler.org/";
    public string AuthMode { get; set; } = "Cookie";
    public string Cookie { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiUsername { get; set; } = "";
    public int IntervalMinutes { get; set; } = 15;
    public bool CloseToTray { get; set; } = true;
    public int RepeatMinutes { get; set; } = 0;
    public RobotSettings WeCom { get; set; } = new();
    public RobotSettings DingTalk { get; set; } = new();
    public RobotSettings Feishu { get; set; } = new();
    public Uri BaseUri => new(ForumUrl.TrimEnd('/') + "/");
    public void Validate(bool requireAuth = true)
    {
        ValidateAuthentication(requireAuth);
        if (IntervalMinutes < 1 || IntervalMinutes > 1440) throw new InvalidOperationException("检查间隔应为 1–1440 分钟。");
        if (RepeatMinutes < 0 || RepeatMinutes > 10080) throw new InvalidOperationException("重复提醒应为 0–10080 分钟，0 表示关闭。");
        ValidateRobot(WeCom, "企业微信", "qyapi.weixin.qq.com", "/cgi-bin/webhook/send");
        ValidateRobot(DingTalk, "钉钉", "oapi.dingtalk.com", "/robot/send");
        ValidateRobot(Feishu, "飞书", "open.feishu.cn", "/open-apis/bot/v2/hook/");
    }
    public void ValidateAuthentication(bool requireAuth = true)
    {
        if (!Uri.TryCreate(ForumUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new InvalidOperationException("论坛地址必须是 HTTPS 地址，不能包含账号、查询参数或片段。");
        if (AuthMode != "Cookie" && AuthMode != "API Key") throw new InvalidOperationException("请选择认证方式。");
        if (requireAuth && string.IsNullOrWhiteSpace(AuthMode == "Cookie" ? Cookie : ApiKey)) throw new InvalidOperationException("请填写所选认证方式的凭据。");
        foreach (var value in new[] { Cookie, ApiKey, ApiUsername })
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("认证信息不能包含换行。");
    }
    public static void ValidatePushChannel(string name, RobotSettings robot)
    {
        switch (name)
        {
            case "企业微信": ValidateRobot(robot, name, "qyapi.weixin.qq.com", "/cgi-bin/webhook/send"); break;
            case "钉钉": ValidateRobot(robot, name, "oapi.dingtalk.com", "/robot/send"); break;
            case "飞书": ValidateRobot(robot, name, "open.feishu.cn", "/open-apis/bot/v2/hook/"); break;
            default: throw new InvalidOperationException("未知推送渠道。");
        }
    }
    private static void ValidateRobot(RobotSettings robot, string name, string host, string path)
    {
        if (!robot.Enabled) return;
        if (!Uri.TryCreate(robot.Webhook, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != host || !uri.AbsolutePath.StartsWith(path, StringComparison.Ordinal) || uri.UserInfo != "")
            throw new InvalidOperationException($"请填写有效的{name}群机器人 HTTPS Webhook。");
    }
}

public sealed class RobotSettings
{
    public bool Enabled { get; set; }
    public string Webhook { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed record ReviewItem(long Id, string Type, string Title, string Author, DateTimeOffset? CreatedAt)
{
    public string CreatedText => CreatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record ReviewPage(List<ReviewItem> Items, string? Next, int? Total);
public sealed class AuthenticationException : Exception
{
    public AuthenticationException(string? reason = null) : base((reason == null ? "" : reason + " ") + "请确认凭据完整、尚未过期，且账号具有审核权限。") { }
}

public static class ReviewParser
{
    public static ReviewPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("reviewables", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("响应缺少审核列表；请检查认证、接口地址或论坛版本。");
        var users = Map(root, "users");
        var topics = Map(root, "topics");
        var items = new List<ReviewItem>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Number(row, "id") ?? throw new InvalidOperationException("审核项缺少 ID，已停止本次检查以避免漏报。");
            var payload = Object(row, "payload");
            var topic = Object(row, "topic");
            if (topic.ValueKind != JsonValueKind.Object && Number(row, "topic_id") is long topicId) topics.TryGetValue(topicId, out topic);
            var author = Object(row, "target_created_by");
            var authorId = Number(row, "target_created_by_id") ?? Number(row, "target_created_by");
            if (author.ValueKind != JsonValueKind.Object && authorId is long uid) users.TryGetValue(uid, out author);
            var type = Text(row, "type") ?? "未知类型";
            // For flagged content created_by can be the reporter; never label that person as its author.
            if (author.ValueKind != JsonValueKind.Object && type == "ReviewableQueuedPost")
            {
                author = Object(row, "created_by");
                var creatorId = Number(row, "created_by_id") ?? Number(row, "created_by");
                if (author.ValueKind != JsonValueKind.Object && creatorId is long cid) users.TryGetValue(cid, out author);
            }
            string authorName = Text(author, "username") ?? Text(payload, "username") ?? "未知作者";
            string title = Text(payload, "title") ?? Text(topic, "title") ?? Text(row, "title") ?? Text(row, "fancy_title")
                ?? (type == "ReviewableUser" ? "用户审核：" + authorName : "审核项 #" + id);
            string label = type switch
            {
                "ReviewableQueuedPost" => Number(row, "topic_id") is null ? "待审主题" : "待审回复",
                "ReviewableFlaggedPost" => "举报帖子",
                "ReviewableUser" => "用户审核",
                _ => type
            };
            DateTimeOffset? created = DateTimeOffset.TryParse(Text(row, "created_at"), out var date) ? date : null;
            items.Add(new(id, label, WebUtility.HtmlDecode(title), authorName, created));
        }
        var meta = Object(root, "meta");
        return new(items, Text(meta, "load_more_reviewables"), (int?)Number(meta, "total_rows_reviewables"));
    }

    private static Dictionary<long, JsonElement> Map(JsonElement root, string key)
    {
        var result = new Dictionary<long, JsonElement>();
        if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var value in list.EnumerateArray()) if (Number(value, "id") is long id) result[id] = value;
        return result;
    }
    public static JsonElement Object(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    public static string? Text(JsonElement value, string key) { var child = Object(value, key); return child.ValueKind == JsonValueKind.String ? child.GetString() : null; }
    public static long? Number(JsonElement value, string key)
    {
        var child = Object(value, key);
        if (child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out long n)) return n;
        if (child.ValueKind == JsonValueKind.String && long.TryParse(child.GetString(), out n)) return n;
        return null;
    }
}

public sealed class ForumClient : IDisposable
{
    private readonly HttpClient client;
    public ForumClient(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public async Task<List<ReviewItem>> FetchAsync(Settings settings, CancellationToken ct)
    {
        Uri? next = new(settings.BaseUri, "review.json?status=pending");
        var visited = new HashSet<string>();
        var all = new Dictionary<long, ReviewItem>();
        while (next != null)
        {
            if (next.Scheme != settings.BaseUri.Scheme || next.Authority != settings.BaseUri.Authority)
                throw new InvalidOperationException("审核分页跳到了其他站点，已拒绝发送认证信息。");
            if (!visited.Add(next.AbsoluteUri) || visited.Count > 1000) throw new InvalidOperationException("审核分页异常，本次结果未更新。");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("openEulerReviewMonitor/1.0");
            if (settings.AuthMode == "Cookie")
            {
                string cookie = settings.Cookie.Trim();
                if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie[7..].Trim();
                request.Headers.Add("Cookie", cookie);
            }
            else
            {
                request.Headers.Add("Api-Key", settings.ApiKey.Trim());
                if (!string.IsNullOrWhiteSpace(settings.ApiUsername)) request.Headers.Add("Api-Username", settings.ApiUsername.Trim());
            }
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AuthenticationException($"论坛返回 HTTP {(int)response.StatusCode}（未认证或访问被拒绝）。");
            if ((int)response.StatusCode is >= 300 and < 400) throw new AuthenticationException($"论坛返回 HTTP {(int)response.StatusCode} 重定向，未获得审核列表。");
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("论坛请求限流（429），请稍后检查或增大间隔。");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"论坛返回 HTTP {(int)response.StatusCode}。");
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.TrimStart().StartsWith('<')) throw new AuthenticationException("论坛返回了 HTML 页面（可能是登录页或访问验证页），而非审核列表。");
            var page = ReviewParser.Parse(body);
            foreach (var item in page.Items) all[item.Id] = item;
            if (!string.IsNullOrWhiteSpace(page.Next))
            {
                var candidate = new Uri(settings.BaseUri, page.Next);
                var builder = new UriBuilder(candidate);
                if (!builder.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) builder.Path = builder.Path.TrimEnd('/') + ".json";
                next = builder.Uri;
            }
            else next = null;
            if (next != null && page.Items.Count == 0) throw new InvalidOperationException("审核分页为空但仍有下一页，本次结果未更新。");
            if (next == null && page.Total is int total && all.Count != total)
                throw new InvalidOperationException("审核列表在分页期间发生变化或返回不完整，请再次检查。");
        }
        return all.Values.OrderByDescending(x => x.Id).ToList();
    }
    public void Dispose() => client.Dispose();
}

public sealed class DeliveryState
{
    public Dictionary<string, HashSet<long>> Delivered { get; set; } = new();
    public Dictionary<string, DateTimeOffset> LastReminder { get; set; } = new();
    public List<ReviewItem> Due(string channel, List<ReviewItem> current, int repeatMinutes, DateTimeOffset now)
    {
        Delivered.TryGetValue(channel, out var seen);
        bool repeat = repeatMinutes > 0 && LastReminder.TryGetValue(channel, out var last) && now - last >= TimeSpan.FromMinutes(repeatMinutes);
        return current.Where(x => repeat || seen == null || !seen.Contains(x.Id)).ToList();
    }
    public void Mark(string channel, IEnumerable<ReviewItem> items, DateTimeOffset now)
    {
        if (!Delivered.TryGetValue(channel, out var ids)) Delivered[channel] = ids = new();
        ids.UnionWith(items.Select(x => x.Id));
        LastReminder[channel] = now;
    }
    public void Prune(List<ReviewItem> current)
    {
        var ids = current.Select(x => x.Id).ToHashSet();
        foreach (var delivered in Delivered.Values) delivered.IntersectWith(ids);
    }
}

