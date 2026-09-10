// End-to-end over HTTP against the real API (in-process Kestrel test server), real database, real Whisper model.
// Only the Microsoft sign-in is substituted (see CallPrepFactory). Model-in-the-loop chat lives in ChatTests.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CallPrep.Tests.E2E;

public class ApiTests(CallPrepFactory app, ITestOutputHelper log) : IClassFixture<CallPrepFactory>
{
    [SkippableFact]
    public async Task Health_is_anonymous_and_reports_fresh_data()
    {
        TestEnv.RequireTunnel();
        var r = await app.ClientAs(null).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(j.GetProperty("ok").GetBoolean());
        var current = DateOnly.Parse(j.GetProperty("sales_current_to").GetString()!);
        log.WriteLine($"model={j.GetProperty("model")} sales_current_to={current}");
        Assert.True(current >= DateOnly.FromDateTime(DateTime.Today).AddDays(-7), $"sales data is stale: {current} (reports_push not running?)");
    }

    [SkippableFact]
    public async Task Root_serves_the_built_ui()
    {
        TestEnv.RequireTunnel();
        var r = await app.ClientAs(null).GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"root\"", html);
        Assert.Contains("Call Prep", html);
    }

    [SkippableFact]
    public async Task Anonymous_api_calls_get_401_json_with_signin_hint_not_a_redirect()
    {
        TestEnv.RequireTunnel();
        var c = app.ClientAs(null);
        foreach (var path in new[] { "/api/me", "/api/admin/users" })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("/signin", j.GetProperty("signin").GetString());
        }
        var chat = await c.PostAsJsonAsync("/api/chat", new { sessionId = "x", message = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, chat.StatusCode);
    }

    [SkippableFact]
    public async Task Signin_redirects_to_microsoft_with_the_pinned_redirect_uri()
    {
        TestEnv.RequireTunnel();
        var r = await app.ClientAs(null).GetAsync("/signin?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var loc = r.Headers.Location!.ToString();
        log.WriteLine(loc[..Math.Min(160, loc.Length)] + "...");
        Assert.StartsWith("https://login.microsoftonline.com/6ea34d6a-c05b-4142-89e3-160ce1904606/oauth2/v2.0/authorize", loc);
        Assert.Contains("client_id=c69f5373-bbaa-4282-85b5-7a00c64aed26", loc);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(Environment.GetEnvironmentVariable("CALLPREP_BASE_URL")!.TrimEnd('/') + "/signin-oidc"), loc);
        Assert.Contains("response_type=code", loc);
        Assert.Contains("code_challenge=", loc);           // PKCE
        Assert.DoesNotContain("response_mode=form_post", loc);   // ASP.NET omits response_mode=query (the default for code flow); form_post would break the Lax cookie
    }

    [SkippableFact]
    public async Task Unknown_and_disabled_accounts_get_403_with_a_helpful_message()
    {
        TestEnv.RequireTunnel();
        foreach (var login in new[] { TestEnv.UnknownLogin, TestEnv.DisabledLogin, Access.ServiceLogin })
        {
            var r = await app.ClientAs(login).GetAsync("/api/me");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(login, j.GetProperty("login").GetString());
            Assert.Contains("not set up", j.GetProperty("detail").GetString());
        }
    }

    [SkippableFact]
    public async Task Me_reports_identity_and_scope_for_admin_and_rep()
    {
        TestEnv.RequireTunnel();
        var a = await app.ClientAs(TestEnv.AdminLogin).GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("admin", a.GetProperty("role").GetString());
        Assert.Equal("all accounts", a.GetProperty("scope").GetString());

        var rep = await app.ClientAs(TestEnv.RepLogin).GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("rep", rep.GetProperty("role").GetString());
        Assert.Equal("25505", rep.GetProperty("salesrepId").GetString());
        Assert.StartsWith("accounts assigned to", rep.GetProperty("scope").GetString());
        log.WriteLine($"rep scope: {rep.GetProperty("scope")}");

        // mixed case + whitespace from the id_token must map to the same row
        var mixed = await app.ClientAs("  DDickman@RaritanGroup.COM ").GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_endpoints_are_admin_only_and_validate_input_without_writing()
    {
        TestEnv.RequireTunnel();
        var rep = app.ClientAs(TestEnv.RepLogin);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PutAsJsonAsync("/api/admin/users", new { login = "x@raritangroup.com", role = "admin", enabled = true })).StatusCode);

        var admin = app.ClientAs(TestEnv.AdminLogin);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.True(list.GetProperty("users").GetArrayLength() >= 10);
        Assert.True(list.GetProperty("salesreps").GetArrayLength() >= 5);
        Assert.Contains(list.GetProperty("users").EnumerateArray(), u => u.GetProperty("login").GetString() == TestEnv.AdminLogin && u.GetProperty("enabled").GetBoolean());

        // every one of these must be refused BEFORE the database (no row changes)
        var bad = new object[]
        {
            new { login = "ddickman", role = "rep", salesrepId = "25505", enabled = true },              // not an email
            new { login = "RSC\\ddickman", role = "rep", salesrepId = "25505", enabled = true },        // old domain form
            new { login = "x@raritangroup.com", role = "owner", enabled = true },                      // bad role
            new { login = "x@raritangroup.com", role = "rep", enabled = true },                        // rep without id
            new { login = Access.ServiceLogin, role = "manager", enabled = true },                     // internal account
            new { login = TestEnv.AdminLogin, role = "rep", salesrepId = "25505", enabled = true },    // self-demotion
            new { login = TestEnv.AdminLogin, role = "admin", enabled = false },                       // self-disable
        };
        foreach (var b in bad)
        {
            var r = await admin.PutAsJsonAsync("/api/admin/users", b);
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(b)} -> {(int)r.StatusCode} {body}");
            log.WriteLine($"refused: {JsonSerializer.Serialize(b)} -> {body}");
        }
        // x@ must not have been created by any of the above
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.DoesNotContain(after.GetProperty("users").EnumerateArray(), u => u.GetProperty("login").GetString() == "x@raritangroup.com");
    }


    [SkippableFact]
    public async Task Today_list_is_scoped_ranked_and_carries_a_question_per_row()
    {
        TestEnv.RequireTunnel();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.ClientAs(null).GetAsync("/api/today")).StatusCode);

        var repRes = await app.ClientAs(TestEnv.RepLogin).GetAsync("/api/today?refresh=true");
        var repBody = await repRes.Content.ReadAsStringAsync();
        Assert.True(repRes.StatusCode == HttpStatusCode.OK, $"{(int)repRes.StatusCode}: {repBody[..Math.Min(1500, repBody.Length)]}");
        var rep = JsonDocument.Parse(repBody).RootElement;
        Assert.StartsWith("accounts assigned to", rep.GetProperty("scope").GetString());
        var sections = rep.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(new[] { "stale_quotes", "going_quiet", "reorder_due", "category_gaps", "tasks", "new_accounts" }, sections.Select(s => s.GetProperty("key").GetString()));
        var rows = sections.Where(s => s.GetProperty("key").GetString() != "tasks").SelectMany(s => s.GetProperty("rows").EnumerateArray()).ToList();   // tasks may sit on accounts outside the book, on purpose
        Assert.True(rows.Count > 0, "rep list is empty");
        log.WriteLine($"rep list: {rows.Count} rows in {rep.GetProperty("ms")} ms");
        foreach (var r in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("customer_name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("question").GetString()));
            Assert.Contains(r.GetProperty("customer_name").GetString()!, r.GetProperty("question").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        // every customer on a rep's list must be in the rep's book (the views are scoped; prove it end to end)
        using (Db.As(TestEnv.RepLogin))
        {
            var db = app.Services.GetRequiredService<Db>();
            foreach (var id in rows.Select(r => r.GetProperty("customer_id").GetString()).Distinct())
                Assert.Equal("1", await db.Scalar($"SELECT count(*) FROM callprep.customer WHERE customer_id::text = '{id}'"));
        }
        foreach (var s in sections) log.WriteLine($"  {s.GetProperty("title")}: {s.GetProperty("rows").GetArrayLength()} - " + string.Join(" | ", s.GetProperty("rows").EnumerateArray().Take(2).Select(r => $"{r.GetProperty("customer_name")} ({r.GetProperty("dollars")})")));

        // second call is served from cache (fast) and identical
        var sw = Stopwatch.StartNew();
        var again = await app.ClientAs(TestEnv.RepLogin).GetFromJsonAsync<JsonElement>("/api/today");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"cached call took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(rep.GetProperty("generated_at").GetString(), again.GetProperty("generated_at").GetString());

        // admin sees all accounts; settings endpoint is admin-only
        var admin = await app.ClientAs(TestEnv.AdminLogin).GetFromJsonAsync<JsonElement>("/api/today?refresh=true");
        Assert.Equal("all accounts", admin.GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.ClientAs(TestEnv.RepLogin).GetAsync("/api/admin/list-settings")).StatusCode);
        var settings = await app.ClientAs(TestEnv.AdminLogin).GetFromJsonAsync<JsonElement>("/api/admin/list-settings");
        Assert.True(settings.GetArrayLength() >= 13);
    }


    [SkippableFact]
    public async Task Tasks_api_create_list_and_complete_go_through_the_outbox()
    {
        TestEnv.RequireTunnel();
        var admin = app.ClientAs(TestEnv.AdminLogin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.ClientAs(null).GetAsync("/api/tasks")).StatusCode);

        var types = await admin.GetFromJsonAsync<JsonElement>("/api/task-types");
        Assert.True(types.GetArrayLength() >= 5);
        var people = await admin.GetFromJsonAsync<JsonElement>("/api/people");
        Assert.Contains(people.EnumerateArray(), x => x.GetProperty("login").GetString() == TestEnv.RepLogin);

        // validation, no writes
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/tasks", new { customerId = "10046", assignedTo = TestEnv.RepLogin, subject = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/tasks", new { customerId = "10046", assignedTo = TestEnv.RepLogin, subject = "x", dueDate = "Friday" })).StatusCode);
        var badAssignee = await admin.PostAsJsonAsync("/api/tasks", new { customerId = "10046", assignedTo = TestEnv.UnknownLogin, subject = "[test] bad assignee", test = true });
        Assert.Equal(HttpStatusCode.BadRequest, badAssignee.StatusCode);
        Assert.Contains("not a Call Prep user", await badAssignee.Content.ReadAsStringAsync());

        // create as a TEST row (admin only): recorded, visible to the assignee, never sent to P21
        var created = await admin.PostAsJsonAsync("/api/tasks", new { customerId = "10046", assignedTo = TestEnv.RepLogin, subject = "[test] api create", activityId = "QUOTE FU", comments = "from ApiTests", dueDate = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), test = true });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.OK, body);
        var j = JsonDocument.Parse(body).RootElement;
        Assert.True(j.GetProperty("test").GetBoolean());
        var id = j.GetProperty("id").GetInt64();

        var mine = await app.ClientAs(TestEnv.RepLogin).GetFromJsonAsync<JsonElement>("/api/tasks");
        var row = mine.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("outbox_id", out var o) && o.ValueKind == JsonValueKind.Number && o.GetInt64() == id);
        Assert.True(row.ValueKind == JsonValueKind.Object, "assignee does not see the new task");
        Assert.Equal("[test] api create", row.GetProperty("subject").GetString());
        log.WriteLine($"outbox #{id} visible to {TestEnv.RepLogin}: {row}");

        // a rep cannot flag a test row (admin only): it becomes a real pending row... so a rep POST is not exercised here.
        // complete: unknown/not-visible task number is refused by the database guard
        var bad = await app.ClientAs(TestEnv.RepLogin).PostAsync("/api/tasks/999999999/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not visible", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/tasks/abc/complete", null)).StatusCode);

        // week list carries a tasks section for the assignee
        var today = await app.ClientAs(TestEnv.RepLogin).GetFromJsonAsync<JsonElement>("/api/today?refresh=true");
        var tasks = today.GetProperty("sections").EnumerateArray().First(sct => sct.GetProperty("key").GetString() == "tasks");
        Assert.True(tasks.GetProperty("count").GetInt32() >= 1);
        Assert.Contains(tasks.GetProperty("rows").EnumerateArray(), r => r.GetProperty("detail").GetString()!.Contains("[test] api create"));
    }

    [SkippableFact]
    public async Task Transcribe_recognises_the_sample_recording_on_our_own_server()
    {
        TestEnv.RequireTunnel();
        var wav = Path.Combine(TestEnv.RepoRoot, "models", "test_buist.wav");
        Skip.IfNot(File.Exists(wav), "models/test_buist.wav missing");
        var c = app.ClientAs(TestEnv.AdminLogin);
        var content = new ByteArrayContent(await File.ReadAllBytesAsync(wav));
        content.Headers.ContentType = new("audio/wav");
        c.DefaultRequestHeaders.Add("X-Session", TestEnv.TestSessionPrefix + "transcribe");
        var r = await c.PostAsync("/api/transcribe", content);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode} {body}");
        var j = JsonDocument.Parse(body).RootElement;
        log.WriteLine($"transcript: \"{j.GetProperty("text")}\" in {j.GetProperty("ms")} ms");
        Assert.Contains("buist", j.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.True(j.GetProperty("ms").GetInt64() < 15_000, "transcription slower than 15 s");
    }

    [SkippableFact]
    public async Task Transcribe_rejects_empty_audio_and_anonymous_calls()
    {
        TestEnv.RequireTunnel();
        var tiny = new ByteArrayContent(new byte[100]);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.ClientAs(TestEnv.AdminLogin).PostAsync("/api/transcribe", tiny)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.ClientAs(null).PostAsync("/api/transcribe", new ByteArrayContent(new byte[5000]))).StatusCode);
    }

    [SkippableFact]
    public async Task Reset_is_idempotent()
    {
        TestEnv.RequireTunnel();
        var c = app.ClientAs(TestEnv.AdminLogin);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/reset", new { sessionId = "never-existed", message = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/reset", new { sessionId = (string?)null, message = "" })).StatusCode);
    }
}
