using System.Net;
using System.Text.Json;
using ForumReviewMonitor;

int passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Assert(true, name); return; }
    throw new Exception("FAIL (did not throw): " + name);
}
var json = """
{"reviewables":[
  {"id":1,"type":"ReviewableQueuedPost","payload":{"title":"Hello &amp; world"},"created_by_id":7,"created_at":"2026-09-05T01:00:00Z"},
  {"id":2,"type":"ReviewableFlaggedPost","topic_id":4,"created_by_id":9,"target_created_by_id":7},
  {"id":3,"type":"PluginReviewable","target_created_by":{"username":"plugin-author"}},
  {"id":4,"type":"ReviewableQueuedPost","topic_id":4,"created_by_id":7},
  {"id":5,"type":"ReviewableUser","target_created_by_id":7},
  {"id":6,"type":"ReviewableFlaggedPost","created_by_id":9}
],"users":[{"id":7,"username":"alice"},{"id":9,"username":"reporter"}],"topics":[{"id":4,"title":"Existing topic"}],"meta":{"total_rows_reviewables":6}}
""";
var page = ReviewParser.Parse(json);
Assert(page.Items.Count == 6 && page.Total == 6, "parse all types");
Assert(page.Items[0].Title == "Hello & world" && page.Items[0].Author == "alice", "queued title and creator");
Assert(page.Items[1].Title == "Existing topic" && page.Items[1].Author == "alice", "flagged author is not reporter");
Assert(page.Items[2].Type == "PluginReviewable" && page.Items[2].Author == "plugin-author", "unknown plugin type retained");
Assert(page.Items[3].Type == "待审回复" && page.Items[4].Title == "用户审核：alice", "reply and user handling");
Assert(page.Items[5].Author == "未知作者", "do not misattribute missing author to reporter");
await Throws<InvalidOperationException>(() => Task.FromResult(ReviewParser.Parse("{\"errors\":[\"login\"]}")), "invalid response is not empty queue");

var now = DateTimeOffset.UtcNow;
var one = new ReviewItem(1, "post", "A", "a", null);
var two = new ReviewItem(2, "post", "B", "b", null);
var state = new DeliveryState();
Assert(state.Due("Windows", [one], 0, now).Count == 1, "first snapshot notifies");
state.Mark("Windows", [one], now);
Assert(state.Due("Windows", [one], 0, now).Count == 0, "unchanged item suppressed");
Assert(state.Due("Windows", [two], 0, now).Single().Id == 2, "same count new ID notifies");
Assert(state.Due("飞书", [one], 0, now).Count == 1, "channel failure independent");
Assert(state.Due("Windows", [one], 30, now.AddMinutes(31)).Count == 1, "optional repeat reminder");
state = JsonSerializer.Deserialize<DeliveryState>(JsonSerializer.Serialize(state))!;
Assert(state.Due("Windows", [one], 0, now).Count == 0, "dedup survives restart");
state.Prune([]);
Assert(state.Due("Windows", [one], 0, now).Count == 1, "returned pending item notifies again");

var settings = new Settings { Cookie = "_t=test", ForumUrl = "https://forum.openeuler.org/" };
var authOnly = new Settings { Cookie = "_t=test", IntervalMinutes = -1, WeCom = new() { Enabled = true, Webhook = "invalid" } };
authOnly.ValidateAuthentication();
Assert(true, "authentication validation independent of monitor and webhook settings");
await Throws<InvalidOperationException>(() => { new Settings().ValidateAuthentication(); return Task.CompletedTask; }, "empty credentials produce explicit failure");
await Throws<InvalidOperationException>(() => { new Settings { AuthMode = "API Key", Cookie = "existing-cookie" }.ValidateAuthentication(); return Task.CompletedTask; }, "API mode requires API key even when Cookie exists");
int calls = 0;
using (var client = new ForumClient(new FakeHandler(request =>
{
    calls++;
    Assert(request.Headers.GetValues("Cookie").Single() == "_t=test", "cookie header attached");
    if (calls == 1) return Reply("""{"reviewables":[{"id":10,"type":"Other"}],"meta":{"total_rows_reviewables":2,"load_more_reviewables":"/review?status=pending&offset=10"}}""");
    Assert(request.RequestUri!.AbsolutePath == "/review.json" && request.RequestUri.Query.Contains("offset=10"), "pagination follows server cursor as JSON");
    return Reply("""{"reviewables":[{"id":11,"type":"Other"}],"meta":{"total_rows_reviewables":2}}""");
}))) Assert((await client.FetchAsync(settings, default)).Count == 2 && calls == 2, "fetch all pages");
using (var client = new ForumClient(new FakeHandler(_ => Reply("""{"reviewables":[],"meta":{"total_rows_reviewables":4}}"""))))
    await Throws<InvalidOperationException>(() => client.FetchAsync(settings, default), "incomplete snapshot rejected");
using (var client = new ForumClient(new FakeHandler(_ => new(HttpStatusCode.Forbidden))))
    await Throws<AuthenticationException>(() => client.FetchAsync(settings, default), "403 requires credentials");
using (var client = new ForumClient(new FakeHandler(_ => Reply("<html>login</html>"))))
    await Throws<AuthenticationException>(() => client.FetchAsync(settings, default), "HTML login not parsed as zero");
using (var client = new ForumClient(new FakeHandler(_ => Reply("""{"reviewables":[{"id":1}],"meta":{"load_more_reviewables":"https://evil.example/review?offset=10"}}"""))))
    await Throws<InvalidOperationException>(() => client.FetchAsync(settings, default), "cross origin pagination cannot leak cookie");
settings.AuthMode = "API Key"; settings.ApiKey = "test-key"; settings.ApiUsername = "moderator";
using (var client = new ForumClient(new FakeHandler(request =>
{
    Assert(!request.Headers.Contains("Cookie") && request.Headers.GetValues("Api-Key").Single() == "test-key" && request.Headers.GetValues("Api-Username").Single() == "moderator", "API key mode never sends Cookie");
    return Reply("""{"reviewables":[],"meta":{"total_rows_reviewables":0}}""");
}))) await client.FetchAsync(settings, default);

var bot = new RobotSettings { Enabled = true, Webhook = "https://open.feishu.cn/open-apis/bot/v2/hook/test", Secret = "test-secret" };
var built = RobotClient.Build("飞书", bot, "测试", DateTimeOffset.FromUnixTimeSeconds(1600000000));
using (var doc = JsonDocument.Parse(built.Json))
{
    Assert(doc.RootElement.GetProperty("timestamp").GetString() == "1600000000" && doc.RootElement.GetProperty("content").GetProperty("text").GetString() == "测试", "Feishu payload timestamp and text");
}
Assert(RobotClient.DingSign("test-secret", 1600000000000).Length == 44 && RobotClient.FeishuSign("test-secret", 1600000000).Length == 44, "signatures base64 SHA256 length");
using (var client = new RobotClient(new FakeHandler(_ => Reply("{\"code\":19024}"))))
    await Throws<InvalidOperationException>(() => client.SendAsync("飞书", bot, "test", default), "HTTP 200 business error is failure");
using (var client = new RobotClient(new FakeHandler(_ => Reply("{}"))))
    await Throws<InvalidOperationException>(() => client.SendAsync("飞书", bot, "test", default), "missing acknowledgement is failure");
using (var client = new RobotClient(new FakeHandler(_ => Reply("{\"errcode\":0}"))))
    await client.SendAsync("企业微信", bot, "test", default);
Assert(true, "WeCom successful acknowledgement");
if (OperatingSystem.IsWindows())
{
    var clear = System.Text.Encoding.UTF8.GetBytes("private-cookie-value");
    var encrypted = Dpapi.Transform(clear, true);
    Assert(!encrypted.SequenceEqual(clear) && Dpapi.Transform(encrypted, false).SequenceEqual(clear), "Windows DPAPI roundtrip");
}
Console.WriteLine($"All {passed} assertions passed.");
static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
