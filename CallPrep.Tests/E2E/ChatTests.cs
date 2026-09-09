// Model-in-the-loop: the full /api/chat SSE stream for the demo customers, exactly the request the page makes.
// Opt-in (CALLPREP_LIVE=1) because every question costs Anthropic credits (about $0.10-0.20 on Opus 5 at medium effort).
// CALLPREP_LIVE_CUSTOMERS="name;name" narrows the sweep; default = first 3 demo customers.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit.Abstractions;

namespace CallPrep.Tests.E2E;

public class ChatTests(CallPrepFactory app, ITestOutputHelper log) : IClassFixture<CallPrepFactory>
{
    public static IEnumerable<object[]> LiveCustomers() =>
        (Environment.GetEnvironmentVariable("CALLPREP_LIVE_CUSTOMERS") is { Length: > 0 } s
            ? s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : TestEnv.DemoCustomers.Take(3)).Select(c => new object[] { c });

    sealed record Turn(string Text, List<string> Tools, List<string> Errors, long Ms, long InTok, long OutTok, string Session);

    async Task<Turn> Ask(HttpClient c, string session, string question)
    {
        var sw = Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new { sessionId = session, message = question }) };
        using var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType!.ToString());
        var text = new System.Text.StringBuilder(); var tools = new List<string>(); var errors = new List<string>();
        long ms = 0, inTok = 0, outTok = 0; string sid = ""; bool done = false;
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var ev = JsonDocument.Parse(line[6..]).RootElement;   // every event must be valid JSON, like the page assumes
            switch (ev.GetProperty("type").GetString())
            {
                case "text": text.Append(ev.GetProperty("text").GetString()); break;
                case "tool": tools.Add($"{ev.GetProperty("name")}({ev.GetProperty("rows")} rows, {ev.GetProperty("ms")} ms)"); break;
                case "error": errors.Add(ev.GetProperty("text").GetString() ?? "?"); break;
                case "done": done = true; ms = ev.GetProperty("ms").GetInt64(); inTok = ev.GetProperty("input_tokens").GetInt64(); outTok = ev.GetProperty("output_tokens").GetInt64(); sid = ev.GetProperty("session_id").GetString()!; break;
                default: Assert.Fail("unknown event type " + ev); break;
            }
        }
        sw.Stop();
        Assert.True(done || errors.Count > 0, "stream ended without a done event");
        return new Turn(text.ToString(), tools, errors, ms, inTok, outTok, sid);
    }

    [SkippableTheory]
    [MemberData(nameof(LiveCustomers))]
    public async Task Demo_questions_stream_a_clean_answer(string customer)
    {
        TestEnv.RequireTunnel(); TestEnv.RequireLive();
        var c = app.ClientAs(TestEnv.AdminLogin);
        c.Timeout = TimeSpan.FromMinutes(3);
        var session = TestEnv.TestSessionPrefix + Guid.NewGuid().ToString("N");

        var t1 = await Ask(c, session, $"What is {customer} not buying that similar customers are?");
        Report(customer, "gap", t1);
        Assert.Empty(t1.Errors);
        Assert.Contains(t1.Tools, t => t.StartsWith("find_customer"));
        Assert.Contains(t1.Tools, t => t.StartsWith("peer_gap") || t.StartsWith("class_gap") || t.StartsWith("customer_snapshot"));
        Assert.True(t1.Text.Length > 80, "answer too short: " + t1.Text);
        Assert.DoesNotContain("⚠", t1.Text);
        Assert.Equal(session, t1.Session);

        // second turn in the same session: history must carry the customer without re-asking
        var t2 = await Ask(c, session, "Any open quotes I should follow up on before the call?");
        Report(customer, "quotes", t2);
        Assert.Empty(t2.Errors);
        Assert.True(t2.Text.Length > 40, "follow-up too short: " + t2.Text);
    }

    [SkippableFact]
    public async Task Rep_asking_about_another_reps_account_is_told_it_is_not_in_their_book()
    {
        TestEnv.RequireTunnel(); TestEnv.RequireLive();
        var c = app.ClientAs(TestEnv.RepLogin);
        c.Timeout = TimeSpan.FromMinutes(3);
        var t = await Ask(c, TestEnv.TestSessionPrefix + Guid.NewGuid().ToString("N"), "Snapshot of Bennett Brothers Mechanical before my call");
        Report("Bennett Brothers (as rep)", "scoped", t);
        Assert.Empty(t.Errors);
        Assert.Contains(t.Tools, x => x.StartsWith("find_customer(0 rows"));
        Assert.Contains("book", t.Text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Free_form_question_uses_run_select_safely()
    {
        TestEnv.RequireTunnel(); TestEnv.RequireLive();
        var c = app.ClientAs(TestEnv.AdminLogin);
        c.Timeout = TimeSpan.FromMinutes(3);
        var t = await Ask(c, TestEnv.TestSessionPrefix + Guid.NewGuid().ToString("N"), "Which five customers bought the most Bell & Gossett product in the last 12 months? Use a SQL query.");
        Report("company", "run_select", t);
        Assert.Empty(t.Errors);
        Assert.True(t.Text.Length > 40);
    }

    [SkippableFact]
    public async Task Stop_mid_answer_drops_the_question_and_the_session_recovers()
    {
        // The Stop button aborts the fetch; the server sees RequestAborted, removes the half-asked question from the
        // conversation, and the next question in the same session must answer cleanly.
        TestEnv.RequireTunnel(); TestEnv.RequireLive();
        var c = app.ClientAs(TestEnv.AdminLogin);
        c.Timeout = TimeSpan.FromMinutes(3);
        var session = TestEnv.TestSessionPrefix + Guid.NewGuid().ToString("N");
        using (var cts = new CancellationTokenSource())
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new { sessionId = session, message = "Snapshot of Buiat before my call" }) };   // typo on purpose
            using var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
            var first = await reader.ReadLineAsync(cts.Token);       // wait for the first tool event, then hang up like the browser does
            log.WriteLine("first event before stop: " + first);
            cts.Cancel();
        }
        await Task.Delay(500);
        var t = await Ask(c, session, "Snapshot of Buist before my call");
        Report("Buist after a stopped typo", "recovered", t);
        Assert.Empty(t.Errors);
        Assert.Contains(t.Tools, x => x.StartsWith("find_customer"));
        Assert.Contains("Buist", t.Text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Nearby_question_uses_the_customers_own_addresses()
    {
        TestEnv.RequireTunnel(); TestEnv.RequireLive();
        var c = app.ClientAs(TestEnv.AdminLogin);
        c.Timeout = TimeSpan.FromMinutes(3);
        var t = await Ask(c, TestEnv.TestSessionPrefix + Guid.NewGuid().ToString("N"), "I'm visiting Walsh Construction today, what other companies are in the area that I can also visit?");
        Report("Walsh Construction", "nearby", t);
        Assert.Empty(t.Errors);
        Assert.Contains(t.Tools, x => x.StartsWith("customers_near"));
        Assert.True(t.Text.Length > 80);
    }

    void Report(string customer, string kind, Turn t)
    {
        log.WriteLine($"── {customer} · {kind} · {t.Ms / 1000.0:F1} s · in {t.InTok} / out {t.OutTok} tokens");
        log.WriteLine("tools: " + string.Join(" → ", t.Tools));
        if (t.Errors.Count > 0) log.WriteLine("ERRORS: " + string.Join(" | ", t.Errors));
        log.WriteLine(t.Text.Trim());
        log.WriteLine("");
    }
}
