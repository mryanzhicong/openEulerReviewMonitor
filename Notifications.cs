using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ForumReviewMonitor;

public sealed class RobotClient : IDisposable
{
    private readonly HttpClient client;
    public RobotClient(HttpMessageHandler? handler = null) => client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public static string DingSign(string secret, long timestamp) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "\n" + secret)));
    public static string FeishuSign(string secret, long timestamp) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(timestamp + "\n" + secret), Array.Empty<byte>()));
    public static (string Url, string Json) Build(string channel, RobotSettings robot, string text, DateTimeOffset now)
    {
        string url = robot.Webhook;
        var payload = new Dictionary<string, object>();
        if (channel == "飞书")
        {
            payload["msg_type"] = "text";
            payload["content"] = new { text };
            if (!string.IsNullOrWhiteSpace(robot.Secret))
            {
                long timestamp = now.ToUnixTimeSeconds();
                payload["timestamp"] = timestamp.ToString();
                payload["sign"] = FeishuSign(robot.Secret, timestamp);
            }
        }
        else
        {
            payload["msgtype"] = "text";
            payload["text"] = new { content = text };
            if (channel == "钉钉" && !string.IsNullOrWhiteSpace(robot.Secret))
            {
                long timestamp = now.ToUnixTimeMilliseconds();
                url += (url.Contains('?') ? "&" : "?") + "timestamp=" + timestamp + "&sign=" + Uri.EscapeDataString(DingSign(robot.Secret, timestamp));
            }
        }
        return (url, JsonSerializer.Serialize(payload));
    }
    public async Task SendAsync(string channel, RobotSettings robot, string text, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var request = Build(channel, robot, text, DateTimeOffset.UtcNow);
                using var body = new StringContent(request.Json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(request.Url, body, ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{channel}返回 HTTP {(int)response.StatusCode}。");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                var code = channel == "飞书" ? ReviewParser.Number(root, "code") ?? ReviewParser.Number(root, "StatusCode") : ReviewParser.Number(root, "errcode");
                if (code != 0) throw new InvalidOperationException($"{channel}未确认发送成功（错误码 {code?.ToString() ?? "缺失"}），请检查 Webhook、安全关键词和签名。");
                return;
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
    }
    public void Dispose() => client.Dispose();
}

public static class NotificationText
{
    public static string Create(List<ReviewItem> items, int total, Uri forum)
    {
        var b = new StringBuilder($"openEuler 审核提醒\n本次需提醒 {items.Count} 项，当前待处理 {total} 项。\n");
        foreach (var item in items.Take(5)) b.AppendLine($"[{Clip(item.Type, 24)}] {Clip(item.Title, 50)} — {Clip(item.Author, 24)}");
        if (items.Count > 5) b.AppendLine($"另有 {items.Count - 5} 项，请打开审核列表查看。");
        b.Append(new Uri(forum, "review?status=pending"));
        return b.ToString();
    }
    public static string Clip(string text, int max) => string.Concat(text.Replace('\r', ' ').Replace('\n', ' ').EnumerateRunes().Take(max).Select(x => x.ToString()));
}

