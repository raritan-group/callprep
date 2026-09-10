using System.Text.Json;

namespace CallPrep.Tests.Unit;

public class AccessTests
{
    [Theory]
    [InlineData("DDickman@RaritanGroup.com", "ddickman@raritangroup.com")]
    [InlineData("  Joel@RaritanValve.com  ", "joel@raritanvalve.com")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeLogin_lowercases_and_trims_keeping_the_domain(string? input, string expected)
    {
        Assert.Equal(expected, Access.NormalizeLogin(input));
    }

    [Fact]
    public void Service_login_is_not_an_email_so_it_can_never_match_an_entra_identity()
    {
        Assert.DoesNotContain("@", Access.ServiceLogin);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("manager", true)]
    [InlineData("rep", false)]
    public void SeesAll_follows_role(string role, bool seesAll)
    {
        var u = new AccessUser("x@raritangroup.com", "X", role, "25505", "DOUG DICKMAN");
        Assert.Equal(seesAll, u.SeesAll);
        Assert.Equal(seesAll ? "all accounts" : "accounts assigned to DOUG DICKMAN", u.ScopeText);
    }

    [Fact]
    public void ScopeText_falls_back_to_rep_id_when_name_unresolved()
    {
        var u = new AccessUser("x@raritangroup.com", null, "rep", "99999", null);
        Assert.Equal("accounts assigned to rep 99999", u.ScopeText);
    }
}

public class ToolDefinitionTests
{
    static readonly string[] Expected = ["find_customer", "customers_near", "draft_task", "customer_snapshot", "peer_gap", "class_gap", "recent_activity", "open_quotes", "cancelled_quotes", "class_overview", "run_select"];

    [Fact]
    public void All_eleven_tools_are_defined_once()
    {
        var names = Tools.Definitions.Select(t => t.Name).ToArray();
        Assert.Equal(Expected.OrderBy(x => x), names.OrderBy(x => x));
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void Every_required_parameter_exists_in_the_schema()
    {
        foreach (var t in Tools.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description");
            var props = t.InputSchema.Properties!;
            foreach (var r in t.InputSchema.Required!)
                Assert.True(props.ContainsKey(r), $"{t.Name}: required '{r}' is not a declared property");
            foreach (var (k, v) in props)
            {
                Assert.True(v.TryGetProperty("type", out var ty), $"{t.Name}.{k} has no type");
                Assert.Contains(ty.GetString(), new[] { "string", "number", "integer" });
            }
        }
    }

    [Fact]
    public async Task Unknown_tool_returns_an_error_object_instead_of_throwing()
    {
        var tools = new Tools(new Db(new Npgsql.NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=x;Password=y").Build()));
        var (result, sql, rows, _) = await tools.Invoke("nope", JsonSerializer.SerializeToElement(new { }));
        Assert.Null(sql); Assert.Equal(0, rows);
        Assert.Contains("unknown tool", result);
    }

    [Fact]
    public async Task run_select_is_refused_before_touching_the_database()
    {
        // unreachable port: if the guard let it through, Npgsql would throw. It must return {error} without connecting.
        var tools = new Tools(new Db(new Npgsql.NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=x;Password=y;Timeout=1").Build()));
        var (result, _, rows, _) = await tools.Invoke("run_select", JsonSerializer.SerializeToElement(new { sql = "DROP TABLE callprep.audit_log", purpose = "test" }));
        Assert.Equal(0, rows);
        Assert.Contains("\"error\"", result);
    }
}
