// Every tool, for every demo customer, against the real database, with no model in the loop.
// This is the deterministic half of the demo rehearsal: if a tool throws or returns {error} for a customer here,
// the model would have had to explain a failure on stage.

using System.Text.Json;
using Xunit.Abstractions;

namespace CallPrep.Tests.Integration;

public class ToolSweepTests(DbFixture fx, ITestOutputHelper log) : IClassFixture<DbFixture>
{
    static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    public static IEnumerable<object[]> Customers() => TestEnv.DemoCustomers.Select(c => new object[] { c });

    async Task<(string id, string name, string? cls)> Find(string query)
    {
        var (json, sql, rows, ms) = await fx.Tools.Invoke("find_customer", Args(new { query }));
        Assert.True(rows > 0, $"find_customer('{query}') returned no rows (as {Db.Login.Value})");
        var first = JsonDocument.Parse(json).RootElement[0];
        Assert.False(first.TryGetProperty("error", out _), json);
        return (first.GetProperty("customer_id").ToString(), first.GetProperty("customer_name").GetString()!, first.GetProperty("customer_class").ValueKind == JsonValueKind.Null ? null : first.GetProperty("customer_class").GetString());
    }

    static void AssertClean(string tool, string json, int rows)
    {
        using var doc = JsonDocument.Parse(json);   // must be valid JSON for the model
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            Assert.Fail($"{tool} returned an error: {err}");
    }

    [SkippableTheory]
    [MemberData(nameof(Customers))]
    public async Task Every_tool_answers_cleanly_for_demo_customer(string customer)
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (id, name, cls) = await Find(customer);
        log.WriteLine($"{customer} -> {id} {name} [{cls ?? "no class"}]");

        var calls = new (string tool, object args)[]
        {
            ("customer_snapshot", new { customer_id = id }),
            ("peer_gap", new { customer_id = id }),
            ("peer_gap", new { customer_id = id, min_penetration_pct = 30, lookalike_count = 20 }),
            ("class_gap", new { customer_id = id }),
            ("recent_activity", new { customer_id = id }),
            ("recent_activity", new { customer_id = id, months = 24 }),
            ("open_quotes", new { customer_id = id }),
            ("open_quotes", new { customer_id = id, max_age_days = 3650 }),
            ("cancelled_quotes", new { customer_id = id }),
        };
        var failures = new List<string>();
        foreach (var (tool, args) in calls)
        {
            try
            {
                var (json, sql, rows, ms) = await fx.Tools.Invoke(tool, Args(args));
                AssertClean(tool, json, rows);
                Assert.True(ms < 30_000, $"{tool} took {ms} ms (statement_timeout is 30 s)");
                log.WriteLine($"  {tool,-18} {rows,4} rows {ms,6} ms");
                if (tool == "customer_snapshot")
                {
                    Assert.Equal(1, rows);
                    var snap = JsonDocument.Parse(json).RootElement[0];
                    Assert.Equal(name, snap.GetProperty("customer_name").GetString());
                    log.WriteLine($"    sales_12m={snap.GetProperty("sales_12m")} prior={snap.GetProperty("sales_prior_12m")} last_invoice={snap.GetProperty("last_invoice_date")} open_quotes_180d={snap.GetProperty("open_quotes_180d")} open_orders={snap.GetProperty("open_orders")}");
                }
                if (tool == "peer_gap")
                {
                    var root = JsonDocument.Parse(json).RootElement;
                    if (root.TryGetProperty("top_lookalikes", out var la))
                    {
                        Assert.True(la.GetArrayLength() > 0, "peer_gap returned a payload with zero lookalikes");
                        // the customer must never be its own lookalike
                        Assert.DoesNotContain(la.EnumerateArray(), x => x.GetProperty("customer_name").GetString() == name);
                        log.WriteLine("    lookalikes: " + string.Join(", ", la.EnumerateArray().Take(3).Select(x => x.GetProperty("customer_name").GetString())));
                        log.WriteLine("    gaps: " + string.Join(", ", root.GetProperty("gaps").EnumerateArray().Take(4).Select(x => x.GetProperty("product_group_desc").GetString())));
                    }
                    else log.WriteLine("    " + root.GetProperty("note").GetString());
                }
            }
            catch (Exception ex) { failures.Add($"{tool}({JsonSerializer.Serialize(args)}): {ex.Message}"); }
        }
        Assert.True(failures.Count == 0, $"{name}:\n  " + string.Join("\n  ", failures));
    }

    [SkippableFact]
    public async Task Class_overview_works_for_every_class_in_the_data()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (json, rows, _) = await fx.Db.Query("SELECT DISTINCT customer_class FROM callprep.customer WHERE customer_class IS NOT NULL ORDER BY 1", 50);
        var classes = JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.GetProperty("customer_class").GetString()!).ToList();
        Assert.NotEmpty(classes);
        foreach (var cls in classes)
        {
            var (j, _, n, ms) = await fx.Tools.Invoke("class_overview", Args(new { customer_class = cls, top = 10 }));
            AssertClean("class_overview", j, n);
            log.WriteLine($"{cls,-16} {n,3} groups {ms,5} ms");
        }
        // lower-case input must still match (upper() on both sides)
        var (lj, _, ln, _) = await fx.Tools.Invoke("class_overview", Args(new { customer_class = "contractor" }));
        Assert.True(ln > 0, lj);
    }

    [SkippableFact]
    public async Task Fuzzy_lookup_recovers_a_misheard_name()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        // "Boost" is what whisper produced for "Buist" before the vocabulary prompt; the trigram/metaphone path must find it
        var (json, sql, rows, ms) = await fx.Tools.Invoke("find_customer", Args(new { query = "Boost" }));
        AssertClean("find_customer", json, rows);
        Assert.Contains("fuzzy", json);
        Assert.Contains("BUIST", json, StringComparison.OrdinalIgnoreCase);
        log.WriteLine($"fuzzy 'Boost' -> {rows} rows {ms} ms: " + string.Join(", ", JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.GetProperty("customer_name").GetString())));
    }

    [SkippableFact]
    public async Task Nonsense_name_returns_an_empty_list_not_an_error()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (json, _, rows, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "zzqxvwq" }));
        Assert.Equal(0, rows);
        Assert.Equal("[]", json);
    }

    [SkippableFact]
    public async Task Non_customer_with_a_real_word_gets_fuzzy_candidates_all_flagged()
    {
        // "Acme Mechanical" is not a customer. The fallback offers the nearest names (ALT MECHANICAL, D&D MECHANICAL...) and every
        // row carries the fuzzy note + a similarity score, which is what the system prompt relies on to ask before assuming.
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (json, _, rows, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "Acme Mechanical" }));
        Assert.True(rows > 0);
        foreach (var r in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            Assert.Contains("fuzzy", r.GetProperty("note").GetString());
            Assert.True(r.GetProperty("name_similarity").GetDecimal() < 0.8m, "a non-customer must not look like an exact match");
        }
        log.WriteLine("Acme Mechanical -> " + string.Join(", ", JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => $"{r.GetProperty("customer_name")} ({r.GetProperty("name_similarity")})")));
    }

    [SkippableFact]
    public async Task Lookup_by_numeric_id_works()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (json, _, rows, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "10046" }));
        Assert.Equal(1, rows);
        Assert.Contains("BUIST", json);
    }

    [SkippableFact]
    public async Task Snapshot_of_an_unknown_id_is_empty_not_an_error()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        foreach (var tool in new[] { "customer_snapshot", "recent_activity", "open_quotes", "cancelled_quotes", "class_gap", "peer_gap" })
        {
            var (json, _, rows, _) = await fx.Tools.Invoke(tool, Args(new { customer_id = "999999999" }));
            AssertClean(tool, json, rows);
            Assert.Equal(0, rows);
        }
    }

    [SkippableFact]
    public async Task run_select_executes_through_the_guard_and_caps_rows()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.AdminLogin);
        var (json, sql, rows, _) = await fx.Tools.Invoke("run_select", Args(new { sql = "SELECT customer_id, customer_name FROM callprep.customer ORDER BY customer_id", purpose = "cap check" }));
        Assert.Equal(Tools.DefaultRows, rows);
        Assert.EndsWith($"LIMIT {Tools.DefaultRows}", sql);
        // a Postgres error inside a valid-looking statement comes back as {error}, not an exception
        var (ej, _, er, _) = await fx.Tools.Invoke("run_select", Args(new { sql = "SELECT no_such_column FROM callprep.customer", purpose = "error path" }));
        Assert.Equal(0, er);
        Assert.Contains("no_such_column", ej);
        // the unscoped *_all views are revoked from callprep_ro: the escape hatch cannot bypass scoping
        var (aj, _, ar, _) = await fx.Tools.Invoke("run_select", Args(new { sql = "SELECT * FROM callprep.customer_all", purpose = "bypass attempt" }));
        Assert.Equal(0, ar);
        Assert.Contains("error", aj);
    }
}
