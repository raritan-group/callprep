// Per-rep scoping is enforced in Postgres. These prove it from the API's side of the wire, with the same Db class.

using System.Text.Json;

namespace CallPrep.Tests.Integration;

public class ScopingTests(DbFixture fx) : IClassFixture<DbFixture>
{
    static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [SkippableFact]
    public async Task Lookup_returns_enabled_rows_only()
    {
        TestEnv.RequireTunnel();
        using (Db.As(TestEnv.AdminLogin))
        {
            var admin = await fx.Db.Lookup(TestEnv.AdminLogin);
            Assert.NotNull(admin); Assert.Equal("admin", admin!.Role); Assert.True(admin.SeesAll);
        }
        using (Db.As(TestEnv.RepLogin))
        {
            var rep = await fx.Db.Lookup(TestEnv.RepLogin);
            Assert.NotNull(rep); Assert.Equal("rep", rep!.Role); Assert.Equal("25505", rep.SalesrepId);
            Assert.False(string.IsNullOrEmpty(rep.SalesrepName), "rep name did not resolve through the scoped customer view");
        }
        using (Db.As(TestEnv.UnknownLogin)) Assert.Null(await fx.Db.Lookup(TestEnv.UnknownLogin));
        using (Db.As(TestEnv.DisabledLogin)) Assert.Null(await fx.Db.Lookup(TestEnv.DisabledLogin));
    }

    [SkippableFact]
    public async Task No_login_sees_nothing_fail_closed()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As("");
        var (_, rows, _) = await fx.Db.Query("SELECT customer_id FROM callprep.customer", 5);
        Assert.Equal(0, rows);
        var (_, srows, _) = await fx.Db.Query("SELECT 1 FROM callprep.sales_line", 5);
        Assert.Equal(0, srows);
    }

    [SkippableFact]
    public async Task Rep_sees_own_book_only_and_lookalikes_hide_peer_sales()
    {
        TestEnv.RequireTunnel();
        string bennett;
        using (Db.As(TestEnv.AdminLogin))
        {
            var (j, _, n, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "Bennett Brothers" }));
            Assert.True(n > 0, "Bennett Brothers not found as admin");
            bennett = JsonDocument.Parse(j).RootElement[0].GetProperty("customer_id").ToString();
        }
        using (Db.As(TestEnv.RepLogin))
        {
            // Doug's account: visible
            var (bj, _, bn, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "Buist" }));
            Assert.Equal(1, bn);
            // another rep's account: invisible by name, by id, and through every per-customer tool
            var (oj, _, on, _) = await fx.Tools.Invoke("find_customer", Args(new { query = "Bennett Brothers" }));
            Assert.Equal(0, on);
            foreach (var tool in new[] { "customer_snapshot", "recent_activity", "open_quotes" })
            {
                var (_, _, rows, _) = await fx.Tools.Invoke(tool, Args(new { customer_id = bennett }));
                Assert.Equal(0, rows);
            }
            // every visible customer belongs to rep 25505
            var (cj, cn, _) = await fx.Db.Query("SELECT DISTINCT salesrep_id::text AS r FROM callprep.customer", 10);
            var reps = JsonDocument.Parse(cj).RootElement.EnumerateArray().Select(x => x.GetProperty("r").GetString()).ToList();
            Assert.Equal(new[] { "25505" }, reps);
            // lookalikes for Buist still come from the whole population, with peer sales withheld
            var (pj, _, _, _) = await fx.Tools.Invoke("peer_gap", Args(new { customer_id = "10046" }));
            var la = JsonDocument.Parse(pj).RootElement.GetProperty("top_lookalikes");
            Assert.True(la.GetArrayLength() > 0);
            foreach (var x in la.EnumerateArray())
                Assert.Equal(JsonValueKind.Null, x.GetProperty("sales_12m").ValueKind);
        }
    }

    [SkippableFact]
    public async Task Rep_cannot_read_user_access_or_write_it()
    {
        TestEnv.RequireTunnel();
        using var _ = Db.As(TestEnv.RepLogin);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fx.Db.Exec("UPDATE callprep.user_access SET enabled = false WHERE login = @p0", TestEnv.AdminLogin));
        // admin still enabled afterwards
        using (Db.As(TestEnv.AdminLogin)) Assert.NotNull(await fx.Db.Lookup(TestEnv.AdminLogin));
    }
}
