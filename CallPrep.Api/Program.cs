// CallPrep.Api — sales call-prep assistant over the reportsdb read-only semantic layer (schema callprep).
// Local POC: reportsdb reached through an SSH tunnel (127.0.0.1:15432 -> Hetzner 127.0.0.1:5432).
// Model: Anthropic Messages API, manual tool loop. Every question / tool call / SQL / answer -> callprep.audit_log.

// Security model (003_access_control.sql + 004_entra_logins.sql):
//   * Sign-in = the person's Microsoft 365 account (Entra ID, OpenID Connect) on the shared employee app
//     registration — the same sign-on RGCommerce employees use. One employee identity for every internal app;
//     customers are a separate population and never reach this app. No passwords here.
//   * The M365 UPN/email must have an enabled row in callprep.user_access (role rep / manager / admin), else 403.
//   * Every SQL statement runs inside a transaction that first sets callprep.login = <account>; the views in the
//     database filter on it. A rep gets only customers assigned to their P21 salesrep_id; manager/admin get all;
//     no login gets nothing. The model's free-form run_select tool is bound by the same rule.
//   * Admins manage user_access from the page (/api/admin/users); a DB trigger re-checks the admin role.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Npgsql;
using Whisper.net;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Entra ID sign-in on the shared employee registration. ENTRA_* come from the environment (never from a file in the repo).
string entraTenant = Environment.GetEnvironmentVariable("ENTRA_TENANT_ID") ?? throw new Exception("ENTRA_TENANT_ID not set");
string entraClient = Environment.GetEnvironmentVariable("ENTRA_CLIENT_ID") ?? throw new Exception("ENTRA_CLIENT_ID not set");
string entraSecret = Environment.GetEnvironmentVariable("ENTRA_CLIENT_SECRET") ?? throw new Exception("ENTRA_CLIENT_SECRET not set");
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(o =>
    {
        o.Instance = "https://login.microsoftonline.com/";
        o.TenantId = entraTenant;                 // this tenant only: employees. Customer/guest identities never validate here.
        o.ClientId = entraClient;
        o.ClientSecret = entraSecret;
        o.CallbackPath = "/signin-oidc";
        o.SignedOutCallbackPath = "/signout-oidc";
        o.ResponseType = "code";
        o.ResponseMode = "query";                 // no form_post, so the correlation cookie can stay SameSite=Lax (works on http://localhost too)
        o.UsePkce = true;
        o.SaveTokens = false;
        o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile"); o.Scope.Add("email");
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.Events.OnRedirectToIdentityProvider = c =>
        {
            // API calls get a 401 (the page then sends the browser to /signin); only top-level navigations get bounced to Microsoft
            if (c.Request.Path.StartsWithSegments("/api")) { c.Response.StatusCode = 401; c.HandleResponse(); return Task.CompletedTask; }
            // Public base URL pinned by config so the redirect_uri is always the registered https address, whatever the proxy sends
            var baseUrl = Environment.GetEnvironmentVariable("CALLPREP_BASE_URL");
            if (!string.IsNullOrEmpty(baseUrl)) c.ProtocolMessage.RedirectUri = baseUrl.TrimEnd('/') + "/signin-oidc";
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToIdentityProviderForSignOut = c =>
        {
            var baseUrl = Environment.GetEnvironmentVariable("CALLPREP_BASE_URL");
            if (!string.IsNullOrEmpty(baseUrl)) c.ProtocolMessage.PostLogoutRedirectUri = baseUrl.TrimEnd('/') + "/signout-oidc";
            return Task.CompletedTask;
        };
    }, cookie =>
    {
        cookie.Cookie.Name = "callprep.auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(10);
        cookie.SlidingExpiration = true;
        cookie.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) { c.Response.StatusCode = 401; return Task.CompletedTask; } c.Response.Redirect(c.RedirectUri); return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
// behind nginx on Hetzner: trust X-Forwarded-Proto/Host so the redirect_uri is https://callprep.raritangroup.com/...
builder.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost; o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); });

string pgHost = Environment.GetEnvironmentVariable("CALLPREP_PG_HOST") ?? "127.0.0.1";
string pgPort = Environment.GetEnvironmentVariable("CALLPREP_PG_PORT") ?? "15432";
string pgPw   = Environment.GetEnvironmentVariable("PG_PASSWORD_CALLPREP") ?? throw new Exception("PG_PASSWORD_CALLPREP not set (run via run-local.ps1)");
string model  = Environment.GetEnvironmentVariable("CALLPREP_MODEL") ?? "claude-opus-5";
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) throw new Exception("ANTHROPIC_API_KEY not set");

var ds = new NpgsqlDataSourceBuilder($"Host={pgHost};Port={pgPort};Database=reportsdb;Username=callprep_ro;Password={pgPw};Timeout=10;CommandTimeout=35;Pooling=true;Maximum Pool Size=8").Build();
builder.Services.AddSingleton(ds);
builder.Services.AddSingleton(new AnthropicClient());
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Tools>();
builder.Services.AddSingleton<Agent>();
string whisperModel = Environment.GetEnvironmentVariable("CALLPREP_WHISPER_MODEL") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "ggml-base.en.bin"));
builder.Services.AddSingleton(new Transcriber(File.Exists(whisperModel) ? whisperModel : null));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Sign-in / sign-out (top-level navigations). The page sends the browser here after an API 401.
app.MapGet("/signin", (HttpContext ctx, string? returnUrl) =>
{
    var target = string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "/" : returnUrl;
    return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
});
app.MapGet("/signout", (HttpContext ctx) =>
    Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

// Access gate for /api/* (health excepted): M365 identity -> callprep.user_access row -> scope for every DB call in this request.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health")) { await next(); return; }
    if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "sign-in required", signin = "/signin" }); return; }
    var login = Access.NormalizeLogin(ctx.User.FindFirst("preferred_username")?.Value ?? ctx.User.FindFirst("email")?.Value ?? ctx.User.Identity.Name);
    var db = ctx.RequestServices.GetRequiredService<Db>();
    AccessUser? user;
    using (Db.As(login)) user = await db.Lookup(login);
    if (user is null || login == Access.ServiceLogin)
    {
        ctx.Response.StatusCode = 403;
        using (Db.As(login)) await db.Audit(login, "", "denied", new { path = path.Value, identity = ctx.User.Identity.Name });
        await ctx.Response.WriteAsJsonAsync(new { error = "not authorized", login, detail = "This account is not set up for Call Prep. Ask IT to add it." });
        return;
    }
    if (path.StartsWithSegments("/api/admin") && user.Role != "admin")
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "admin only", login });
        return;
    }
    ctx.Items["user"] = user;
    Db.Login.Value = login;           // flows through every await in this request
    await next();
});

// Whisper vocabulary hint: customer names the reps actually say, plus product-group names. Runs as the internal service login.
try
{
    var db0 = app.Services.GetRequiredService<Db>();
    using var _ = Db.As(Access.ServiceLogin);
    var names = await db0.Query(@"SELECT customer_name FROM (SELECT c.customer_name, SUM(p.sales_12m) s FROM callprep.customer c JOIN callprep.customer_pg_12m p ON p.customer_id=c.customer_id GROUP BY 1 ORDER BY s DESC LIMIT 250) t", 250);
    var groups = await db0.Query(@"SELECT DISTINCT product_group_desc FROM callprep.class_pg_penetration WHERE penetration_pct >= 5", 200);
    static string Clean(string j) => string.Join(", ", JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(j)!.Select(r => r.Values.First()?.ToString() ?? "").Where(x => x.Length > 0).Select(x => Regex.Replace(x, @"[^A-Za-z0-9&' .-]", " ").Trim()).Distinct());
    var stt = app.Services.GetRequiredService<Transcriber>();
    stt.Vocabulary = "Raritan Group sales call prep. Customers: " + Clean(names.json) + ". Products: " + Clean(groups.json) + ".";
    Console.WriteLine($"whisper vocabulary: {stt.Vocabulary.Length} chars");
}
catch (Exception ex) { Console.Error.WriteLine($"vocabulary load failed: {ex.Message}"); }

// Serve the built React app when present (CallPrep.Web/dist copied to wwwroot)
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapGet("/api/health", async (Db db) =>
{
    using var _ = Db.As(Access.ServiceLogin);
    var fresh = await db.Scalar("SELECT max(invoice_date)::text FROM callprep.sales_line");
    return Results.Ok(new { ok = true, model, sales_current_to = fresh });
});

// Who am I, what can I see. The page calls this first; 401 = not signed in, 403 = signed in but not set up.
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var u = (AccessUser)ctx.Items["user"]!;
    return Results.Ok(new { u.Login, u.DisplayName, u.Role, u.SalesrepId, u.SalesrepName, scope = u.ScopeText });
});

app.MapPost("/api/chat", async (HttpContext ctx, Agent agent, ChatRequest req) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var user = (AccessUser)ctx.Items["user"]!;
    var session = string.IsNullOrWhiteSpace(req.SessionId) ? Guid.NewGuid().ToString("N") : req.SessionId;
    await foreach (var ev in agent.Run(session, user, req.Message, ctx.RequestAborted))
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev)}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
});

// Speech to text, on this machine. Body = WAV, 16 kHz mono 16-bit PCM (the page records it that way). Audio is not stored.
app.MapPost("/api/transcribe", async (HttpContext ctx, Transcriber stt, Db db) =>
{
    if (!stt.Available) return Results.Problem("speech model not loaded", statusCode: 503);
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
    if (ms.Length < 1000) return Results.BadRequest(new { error = "no audio" });
    ms.Position = 0;
    var sw = Stopwatch.StartNew();
    var text = await stt.Transcribe(ms, ctx.RequestAborted);
    sw.Stop();
    var user = (AccessUser)ctx.Items["user"]!;
    await db.Audit(user.Login, ctx.Request.Headers["X-Session"].FirstOrDefault() ?? "", "transcript", new { text, bytes = ms.Length }, sw.ElapsedMilliseconds);
    return Results.Ok(new { text, ms = sw.ElapsedMilliseconds });
});

app.MapPost("/api/reset", (HttpContext ctx, Agent agent, ChatRequest req) => { agent.Reset((AccessUser)ctx.Items["user"]!, req.SessionId ?? ""); return Results.Ok(); });

// ── admin: who may use Call Prep (gate above already requires role = admin for /api/admin/*) ──
app.MapGet("/api/admin/users", async (Db db) =>
{
    var users = await db.Query("SELECT login, display_name, role, salesrep_id, enabled, notes, updated_at::text AS updated_at, updated_by FROM callprep.user_access ORDER BY role, login", 500);
    var reps = await db.Query("SELECT salesrep_id, salesrep_name, customers FROM callprep.salesrep WHERE salesrep_name IS NOT NULL ORDER BY customers DESC", 200);
    return Results.Content($"{{\"users\":{users.json},\"salesreps\":{reps.json}}}", "application/json");
});

app.MapPut("/api/admin/users", async (HttpContext ctx, Db db, UserAccessEdit e) =>
{
    var me = (AccessUser)ctx.Items["user"]!;
    var login = Access.NormalizeLogin(e.Login);
    if (string.IsNullOrWhiteSpace(login) || !Regex.IsMatch(login, @"^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$")) return Results.BadRequest(new { error = "login must be the person's Microsoft 365 email (e.g. ddickman@raritangroup.com or joel@raritanvalve.com)" });
    if (login == Access.ServiceLogin) return Results.BadRequest(new { error = "internal account" });
    var role = (e.Role ?? "").Trim().ToLowerInvariant();
    if (role is not ("rep" or "manager" or "admin")) return Results.BadRequest(new { error = "role must be rep, manager or admin" });
    var rep = string.IsNullOrWhiteSpace(e.SalesrepId) ? null : e.SalesrepId.Trim();
    if (role == "rep" && rep is null) return Results.BadRequest(new { error = "a rep needs a salesrep_id" });
    if (login == me.Login && (role != "admin" || !e.Enabled)) return Results.BadRequest(new { error = "you cannot remove your own admin access" });
    try
    {
        await db.Exec(@"INSERT INTO callprep.user_access (login, display_name, role, salesrep_id, enabled, notes)
                        VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
                        ON CONFLICT (login) DO UPDATE SET display_name = EXCLUDED.display_name, role = EXCLUDED.role,
                          salesrep_id = EXCLUDED.salesrep_id, enabled = EXCLUDED.enabled, notes = EXCLUDED.notes",
                        login, (object?)e.DisplayName?.Trim() ?? DBNull.Value, role, (object?)rep ?? DBNull.Value, e.Enabled, (object?)e.Notes?.Trim() ?? DBNull.Value);
    }
    catch (PostgresException px) { return Results.BadRequest(new { error = px.MessageText }); }
    await db.Audit(me.Login, "", "access_change", new { login, e.DisplayName, role, salesrep_id = rep, e.Enabled, e.Notes });
    return Results.Ok(new { ok = true, login });
});

app.Run();

// ───────────────────────────── types ─────────────────────────────

record ChatRequest(string? SessionId, string? User, string Message);
record UserAccessEdit(string Login, string? DisplayName, string? Role, string? SalesrepId, bool Enabled, string? Notes);

/// A signed-in person with an enabled callprep.user_access row.
sealed record AccessUser(string Login, string? DisplayName, string Role, string? SalesrepId, string? SalesrepName)
{
    public bool SeesAll => Role is "manager" or "admin";
    public string ScopeText => SeesAll ? "all accounts" : $"accounts assigned to {SalesrepName ?? ("rep " + SalesrepId)}";
}

static class Access
{
    public const string ServiceLogin = "callprep-service";
    /// The M365 UPN / email as the id_token carries it, lower-cased: "DDickman@RaritanGroup.com" -> "ddickman@raritangroup.com".
    /// The domain is kept on purpose: raritangroup.com and raritanvalve.com both exist.
    public static string NormalizeLogin(string? name) => (name ?? "").Trim().ToLowerInvariant();
}

/// Local speech-to-text via whisper.cpp (Whisper.net). One factory, one processor per request.
sealed class Transcriber
{
    readonly WhisperFactory? factory;
    public string Vocabulary { get; set; } = "";
    public bool Available => factory is not null;
    public Transcriber(string? modelPath)
    {
        if (modelPath is null) { Console.Error.WriteLine("whisper model not found; /api/transcribe disabled"); return; }
        factory = WhisperFactory.FromPath(modelPath);
        Console.WriteLine($"whisper model loaded: {modelPath}");
    }
    public async Task<string> Transcribe(Stream wav, CancellationToken ct)
    {
        var b = factory!.CreateBuilder().WithLanguage("en").WithThreads(Math.Max(2, Environment.ProcessorCount / 2));
        if (Vocabulary.Length > 0) b = b.WithPrompt(Vocabulary);
        await using var proc = b.Build();
        var sb = new StringBuilder();
        await foreach (var seg in proc.ProcessAsync(wav, ct)) sb.Append(seg.Text);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}

/// All database access. Every statement runs in its own transaction that first sets callprep.login to the
/// current request's login (AsyncLocal), so the scoped views in the database decide what is visible.
/// No login set -> the setting is '' -> the views return nothing. Fail closed.
sealed class Db(NpgsqlDataSource ds)
{
    public static readonly AsyncLocal<string?> Login = new();

    /// Temporarily run as a given login (service startup, health). Restores the previous value on dispose.
    public static IDisposable As(string login) { var prev = Login.Value; Login.Value = login; return new Restore(() => Login.Value = prev); }
    sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    async Task<(NpgsqlConnection conn, NpgsqlTransaction tx)> Begin()
    {
        var conn = await ds.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await using var sc = new NpgsqlCommand("SELECT set_config('callprep.login', @l, true)", conn, tx);
        sc.Parameters.AddWithValue("l", Login.Value ?? "");
        await sc.ExecuteNonQueryAsync();
        return (conn, tx);
    }

    public async Task<string?> Scalar(string sql)
    {
        var (conn, tx) = await Begin();
        await using (conn) await using (tx)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            var v = await cmd.ExecuteScalarAsync();
            await tx.CommitAsync();
            return v?.ToString();
        }
    }

    public async Task<int> Exec(string sql, params object[] ps)
    {
        var (conn, tx) = await Begin();
        await using (conn) await using (tx)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            for (int i = 0; i < ps.Length; i++) cmd.Parameters.AddWithValue($"p{i}", ps[i]);
            var n = await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return n;
        }
    }

    /// The enabled user_access row for a login, with the rep's name resolved through the (scoped) customer view.
    public async Task<AccessUser?> Lookup(string login)
    {
        var (conn, tx) = await Begin();
        await using (conn) await using (tx)
        {
            await using var cmd = new NpgsqlCommand(@"SELECT u.login, u.display_name, u.role, u.salesrep_id,
                                                        (SELECT max(c.salesrep_name) FROM callprep.customer c WHERE c.salesrep_id::text = u.salesrep_id) AS salesrep_name
                                                       FROM callprep.user_access u WHERE u.login = @l AND u.enabled", conn, tx);
            cmd.Parameters.AddWithValue("l", login);
            await using var rd = await cmd.ExecuteReaderAsync();
            AccessUser? u = null;
            if (await rd.ReadAsync())
                u = new AccessUser(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4));
            await rd.CloseAsync();
            await tx.CommitAsync();
            return u;
        }
    }

    /// Run a SELECT and return rows as a JSON array (max rows enforced). Parameters are @p0..@pN.
    public async Task<(string json, int rows, long ms)> Query(string sql, int maxRows, params object[] ps)
    {
        var sw = Stopwatch.StartNew();
        var (conn, tx) = await Begin();
        await using (conn) await using (tx)
        {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        for (int i = 0; i < ps.Length; i++) cmd.Parameters.AddWithValue($"p{i}", ps[i]);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync() && rows.Count < maxRows)
        {
            var d = new Dictionary<string, object?>(rd.FieldCount);
            for (int i = 0; i < rd.FieldCount; i++)
            {
                var v = rd.IsDBNull(i) ? null : rd.GetValue(i);
                d[rd.GetName(i)] = v switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-dd"),
                    DateOnly dd => dd.ToString("yyyy-MM-dd"),
                    decimal m => Math.Round(m, 2),
                    _ => v
                };
            }
            rows.Add(d);
        }
        await rd.CloseAsync();
        await tx.CommitAsync();
        sw.Stop();
        return (JsonSerializer.Serialize(rows), rows.Count, sw.ElapsedMilliseconds);
        }
    }

    public async Task Audit(string user, string session, string kind, object payload, long? ms = null, int? rows = null)
    {
        try
        {
            await using var cmd = ds.CreateCommand("INSERT INTO callprep.audit_log (user_name, session_id, kind, payload, ms, row_count) VALUES (@u,@s,@k,@p::jsonb,@ms,@r)");
            cmd.Parameters.AddWithValue("u", user);
            cmd.Parameters.AddWithValue("s", session);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("p", JsonSerializer.Serialize(payload));
            cmd.Parameters.AddWithValue("ms", (object?)ms ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", (object?)rows ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine($"audit failed: {ex.Message}"); }
    }
}

/// The semantic-layer tools. Each returns a JSON string for the model. All SQL is parameterized and hits callprep.* only.
sealed class Tools(Db db)
{
    public const int DefaultRows = 200;

    public static readonly Tool[] Definitions =
    [
        T("find_customer", "Find customers by name fragment or customer id. Always call this first when the user names a company; use the returned customer_id for every other tool.",
          new { query = P("string", "Part of the customer name, or the numeric customer id") }, ["query"]),
        T("customer_snapshot", "Profile plus sales totals for a customer: market class, rep, trailing-12-month and prior-12-month sales, lifetime sales, last invoice date, top product groups (12 months), open quotes and open orders totals.",
          new { customer_id = P("string", "Customer id from find_customer") }, ["customer_id"]),
        T("peer_gap", "What similar customers buy that this customer does not. 'Similar' = the 40 customers whose product-group purchase mix is most alike (cosine similarity on trailing-12-month sales), NOT the market-class label. Returns product groups this customer is NOT buying (12 months) that at least min_penetration_pct of those lookalikes do buy, plus the top lookalikes so you can name them. This is the core cross-sell question; use it first.",
          new { customer_id = P("string", "Customer id"), min_penetration_pct = P("number", "Only show product groups bought by at least this percent of lookalikes (default 15)"), lookalike_count = P("integer", "How many lookalikes form the peer group (default 40)") }, ["customer_id"]),
        T("class_gap", "Secondary view of gaps using the P21 market-class label (CONTRACTOR, UTILITY, ...) as the peer group instead of purchase behavior. The class label mixes trades (site contractors and mechanical contractors are both CONTRACTOR), so gaps from this tool often list the other trade's products. Only use when asked for the class comparison explicitly, and caveat it.",
          new { customer_id = P("string", "Customer id"), min_penetration_pct = P("number", "Minimum class penetration percent (default 15)") }, ["customer_id"]),
        T("recent_activity", "Recent invoiced orders for a customer, grouped by invoice: date, order number, invoice total, line count and the top items. Use for 'what have they bought lately'.",
          new { customer_id = P("string", "Customer id"), months = P("integer", "Look-back window in months (default 6)") }, ["customer_id"]),
        T("open_quotes", "Open (unconverted) quotes for a customer grouped by quote number: date, age in days, total value, line count, product groups. P21 never closes quotes, so only recent ones are live; default window is 180 days. Use for 'what is pending' or follow-up items before a call.",
          new { customer_id = P("string", "Customer id"), max_age_days = P("integer", "Only quotes newer than this many days (default 180)") }, ["customer_id"]),
        T("cancelled_quotes", "Quotes this customer cancelled or lost, grouped by cancel reason, with totals. Use to understand why we lost business.",
          new { customer_id = P("string", "Customer id"), months = P("integer", "Look-back window in months (default 12)") }, ["customer_id"]),
        T("class_overview", "Market-class view: for a given customer_class (CONTRACTOR, UTILITY, PROCESS, RESALE, ENERGY, Engineering, Transportation), the most common product groups by penetration and average sales per buyer. Use to describe what a market segment typically buys.",
          new { customer_class = P("string", "Customer class name"), top = P("integer", "How many product groups (default 25)") }, ["customer_class"]),
        T("run_select", "Escape hatch: run a read-only SQL SELECT against the callprep schema when no other tool answers the question. Available views: callprep.customer(customer_id, customer_name, customer_class, salesrep_id, salesrep_name); callprep.sales_line(invoice_no, line_no, order_no, invoice_date, customer_id, customer_name, salesrep_id, taker, item_id, item_desc, qty_shipped, unit_price, extended_price, product_group_id, product_group_desc, supplier_name, location_id, county, ship_city, ship_state); callprep.open_quote_line, callprep.open_order_line, callprep.cancelled_quote_line (same shape, order_no/order_date/qty_ordered/extended_price; cancelled adds cancel_reason_desc); callprep.customer_pg_12m(customer_id, product_group_id, product_group_desc, sales_12m, lines_12m, last_invoice_date); callprep.customer_pg_ltd(... sales_ltd, first_invoice_date, last_invoice_date); callprep.class_pg_penetration(customer_class, product_group_id, product_group_desc, buyers_12m, active_customers, penetration_pct, class_sales_12m, avg_sales_per_buyer). Postgres syntax. Single SELECT or WITH, no semicolons, max 200 rows returned.",
          new { sql = P("string", "The SELECT statement"), purpose = P("string", "One line on what this query answers") }, ["sql", "purpose"]),
    ];

    static object P(string type, string description) => new { type, description };
    static Tool T(string name, string desc, object props, string[] required)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var pr in props.GetType().GetProperties())
            dict[pr.Name] = JsonSerializer.SerializeToElement(pr.GetValue(props));
        return new Tool { Name = name, Description = desc, InputSchema = new() { Properties = dict, Required = required } };
    }

    public async Task<(string result, string? sql, int rows, long ms)> Invoke(string name, JsonElement input)
    {
        string S(string k, string? dflt = null) => input.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : dflt ?? "";
        int I(string k, int dflt) => input.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : dflt;
        double D(string k, double dflt) => input.TryGetProperty(k, out var v) && v.TryGetDouble(out var n) ? n : dflt;

        switch (name)
        {
            case "find_customer":
            {
                var q = S("query").Trim();
                var sql = @"SELECT c.customer_id, c.customer_name, c.customer_class, c.salesrep_name,
                              (SELECT ROUND(COALESCE(SUM(sales_12m),0)) FROM callprep.customer_pg_12m p WHERE p.customer_id=c.customer_id) AS sales_12m,
                              (SELECT MAX(last_invoice_date) FROM callprep.customer_pg_ltd p WHERE p.customer_id=c.customer_id) AS last_invoice
                            FROM callprep.customer c
                            WHERE c.customer_name ILIKE '%' || @p0 || '%' OR c.customer_id::text = @p0
                            ORDER BY sales_12m DESC NULLS LAST, c.customer_name LIMIT 10";
                var r = await db.Query(sql, 10, q);
                if (r.rows == 0 && q.Length >= 3)
                {
                    // fuzzy fallback for spoken / misheard names: trigram similarity + double-metaphone on the first word
                    var fsql = @"SELECT c.customer_id, c.customer_name, c.customer_class, c.salesrep_name,
                                   (SELECT ROUND(COALESCE(SUM(sales_12m),0)) FROM callprep.customer_pg_12m p WHERE p.customer_id=c.customer_id) AS sales_12m,
                                   ROUND(public.similarity(c.customer_name::text, @p0)::numeric, 2) AS name_similarity,
                                   'fuzzy match: confirm with the user if unsure' AS note
                                 FROM callprep.customer c
                                 WHERE public.similarity(c.customer_name::text, @p0) > 0.2
                                    OR public.dmetaphone(split_part(c.customer_name::text,' ',1)) = public.dmetaphone(split_part(@p0,' ',1))
                                 ORDER BY name_similarity DESC, sales_12m DESC NULLS LAST LIMIT 8";
                    var f = await db.Query(fsql, 8, q);
                    return (f.json, fsql, f.rows, r.ms + f.ms);
                }
                return (r.json, sql, r.rows, r.ms);
            }
            case "customer_snapshot":
            {
                var id = S("customer_id").Trim();
                var sql = @"WITH s AS (
                              SELECT SUM(extended_price) FILTER (WHERE invoice_date >= CURRENT_DATE - INTERVAL '12 months') AS sales_12m,
                                     SUM(extended_price) FILTER (WHERE invoice_date >= CURRENT_DATE - INTERVAL '24 months' AND invoice_date < CURRENT_DATE - INTERVAL '12 months') AS sales_prior_12m,
                                     SUM(extended_price) AS sales_lifetime,
                                     COUNT(DISTINCT invoice_no) FILTER (WHERE invoice_date >= CURRENT_DATE - INTERVAL '12 months') AS invoices_12m,
                                     MAX(invoice_date) AS last_invoice_date, MIN(invoice_date) AS first_invoice_date
                              FROM callprep.sales_line WHERE customer_id::text = @p0),
                            q AS (SELECT COUNT(DISTINCT order_no) AS open_quotes_180d, ROUND(COALESCE(SUM(extended_price),0)) AS open_quote_value_180d FROM callprep.open_quote_line WHERE customer_id::text=@p0 AND order_date >= CURRENT_DATE - 180),
                            o AS (SELECT COUNT(DISTINCT order_no) AS open_orders, ROUND(COALESCE(SUM(extended_price),0)) AS open_order_value FROM callprep.open_order_line WHERE customer_id::text=@p0),
                            pg AS (SELECT json_agg(json_build_object('product_group', product_group_desc, 'sales_12m', ROUND(sales_12m), 'last', last_invoice_date) ORDER BY sales_12m DESC) AS top_groups
                                   FROM (SELECT * FROM callprep.customer_pg_12m WHERE customer_id::text=@p0 ORDER BY sales_12m DESC LIMIT 12) t)
                            SELECT c.customer_id, c.customer_name, c.customer_class, c.salesrep_name,
                                   ROUND(s.sales_12m) AS sales_12m, ROUND(s.sales_prior_12m) AS sales_prior_12m, ROUND(s.sales_lifetime) AS sales_lifetime,
                                   s.invoices_12m, s.last_invoice_date, s.first_invoice_date,
                                   q.open_quotes_180d, q.open_quote_value_180d, o.open_orders, o.open_order_value, pg.top_groups
                            FROM callprep.customer c, s, q, o, pg WHERE c.customer_id::text=@p0";
                var r = await db.Query(sql, 1, id);
                return (r.json, sql, r.rows, r.ms);
            }
            case "peer_gap":
            {
                var id = S("customer_id").Trim();
                var minPen = D("min_penetration_pct", 15);
                var n = Math.Clamp(I("lookalike_count", 40), 5, 200);
                var la = await db.Query("SELECT customer_name, customer_class, similarity, sales_12m FROM callprep.lookalikes(@p0, @p1)", 8, id, n);
                var gap = await db.Query("SELECT * FROM callprep.lookalike_gap(@p0, @p1, @p2)", 40, id, n, (decimal)minPen);
                var sqlText = $"callprep.lookalikes('{id}',{n}); callprep.lookalike_gap('{id}',{n},{minPen})";
                if (la.rows == 0)
                    return (JsonSerializer.Serialize(new { note = "No lookalikes found: the customer has no sales in the last 12 months, so there is no purchase mix to compare. Try class_gap or recent_activity with a longer window." }), sqlText, 0, la.ms + gap.ms);
                var payload = $"{{\"peer_group\":\"top {n} customers by purchase-mix similarity\",\"top_lookalikes\":{la.json},\"gaps\":{gap.json}}}";
                return (payload, sqlText, gap.rows, la.ms + gap.ms);
            }
            case "class_gap":
            {
                var id = S("customer_id").Trim();
                var minPen = D("min_penetration_pct", 15);
                var sql = @"WITH me AS (SELECT customer_id, customer_class FROM callprep.customer WHERE customer_id::text=@p0)
                            SELECT p.product_group_id, p.product_group_desc, p.penetration_pct, p.buyers_12m, p.active_customers,
                                   p.avg_sales_per_buyer,
                                   COALESCE(l.sales_ltd,0) AS my_lifetime_sales, l.last_invoice_date AS my_last_purchase,
                                   CASE WHEN l.customer_id IS NULL THEN 'never bought' ELSE 'bought before, not in last 12 months' END AS status
                            FROM callprep.class_pg_penetration p
                            JOIN me ON me.customer_class = p.customer_class
                            LEFT JOIN callprep.customer_pg_12m m ON m.customer_id = me.customer_id AND m.product_group_id = p.product_group_id
                            LEFT JOIN callprep.customer_pg_ltd l ON l.customer_id = me.customer_id AND l.product_group_id = p.product_group_id
                            WHERE m.customer_id IS NULL AND p.penetration_pct >= @p1
                            ORDER BY p.penetration_pct DESC LIMIT 40";
                var r = await db.Query(sql, 40, id, minPen);
                if (r.rows == 0)
                {
                    var cls = await db.Query("SELECT customer_class FROM callprep.customer WHERE customer_id::text=@p0", 1, id);
                    r = (JsonSerializer.Serialize(new { note = "No gaps at that threshold, or the customer has no market class assigned (class is null for about 13% of active customers). Class lookup: " + cls.json }), 0, r.ms);
                }
                return (r.json, sql, r.rows, r.ms);
            }
            case "recent_activity":
            {
                var id = S("customer_id").Trim(); var months = Math.Clamp(I("months", 6), 1, 60);
                var sql = @"SELECT invoice_no, MIN(order_no) AS order_no, invoice_date, ROUND(SUM(extended_price)) AS invoice_total, COUNT(*) AS lines,
                                   MAX(taker) AS taker, MAX(ship_city) AS ship_city,
                                   (array_agg(item_desc ORDER BY extended_price DESC))[1:4] AS top_items,
                                   array_agg(DISTINCT product_group_desc) AS product_groups
                            FROM callprep.sales_line WHERE customer_id::text=@p0 AND invoice_date >= CURRENT_DATE - (@p1 || ' months')::interval
                            GROUP BY invoice_no, invoice_date ORDER BY invoice_date DESC LIMIT 40";
                var r = await db.Query(sql, 40, id, months.ToString());
                return (r.json, sql, r.rows, r.ms);
            }
            case "open_quotes":
            {
                var id = S("customer_id").Trim();
                var maxAge = Math.Clamp(I("max_age_days", 180), 1, 3650);
                var sql = @"SELECT order_no, MIN(order_date) AS quote_date, (CURRENT_DATE - MIN(order_date))::int AS age_days,
                                   ROUND(SUM(extended_price)) AS quote_value, COUNT(*) AS lines, MAX(taker) AS taker, MAX(customer_po) AS customer_po,
                                   array_agg(DISTINCT product_group_desc) AS product_groups
                            FROM callprep.open_quote_line WHERE customer_id::text=@p0 AND order_date >= CURRENT_DATE - @p1
                            GROUP BY order_no ORDER BY quote_value DESC LIMIT 40";
                var r = await db.Query(sql, 40, id, maxAge);
                return (r.json, sql, r.rows, r.ms);
            }
            case "cancelled_quotes":
            {
                var id = S("customer_id").Trim(); var months = Math.Clamp(I("months", 12), 1, 60);
                var sql = @"SELECT COALESCE(cancel_reason_desc, cancel_reason, '(no reason)') AS reason, COUNT(DISTINCT order_no) AS quotes,
                                   ROUND(SUM(extended_price)) AS value, MAX(order_date) AS most_recent,
                                   array_agg(DISTINCT product_group_desc) AS product_groups
                            FROM callprep.cancelled_quote_line WHERE customer_id::text=@p0 AND order_date >= CURRENT_DATE - (@p1 || ' months')::interval
                            GROUP BY 1 ORDER BY value DESC LIMIT 40";
                var r = await db.Query(sql, 40, id, months.ToString());
                return (r.json, sql, r.rows, r.ms);
            }
            case "class_overview":
            {
                var cls = S("customer_class").Trim(); var top = Math.Clamp(I("top", 25), 1, 100);
                var sql = @"SELECT product_group_id, product_group_desc, penetration_pct, buyers_12m, active_customers, avg_sales_per_buyer, ROUND(class_sales_12m) AS class_sales_12m
                            FROM callprep.class_pg_penetration WHERE upper(customer_class)=upper(@p0) ORDER BY penetration_pct DESC LIMIT @p1";
                var r = await db.Query(sql, top, cls, top);
                return (r.json, sql, r.rows, r.ms);
            }
            case "run_select":
            {
                var sql = S("sql").Trim().TrimEnd(';').Trim();
                var err = SqlGuard.Check(sql);
                if (err != null) return (JsonSerializer.Serialize(new { error = err }), sql, 0, 0);
                if (!Regex.IsMatch(sql, @"\blimit\s+\d+\s*$", RegexOptions.IgnoreCase)) sql += $"\nLIMIT {DefaultRows}";
                try
                {
                    var r = await db.Query(sql, DefaultRows);
                    return (r.json, sql, r.rows, r.ms);
                }
                catch (PostgresException px) { return (JsonSerializer.Serialize(new { error = px.MessageText, hint = px.Hint }), sql, 0, 0); }
            }
            default:
                return (JsonSerializer.Serialize(new { error = $"unknown tool {name}" }), null, 0, 0);
        }
    }
}

static class SqlGuard
{
    static readonly Regex Forbidden = new(@"\b(insert|update|delete|drop|alter|create|truncate|grant|revoke|copy|call|do|execute|pg_sleep|pg_read|pg_write|lo_|dblink|set\s|reset\s|vacuum|analyze|listen|notify|refresh)\b", RegexOptions.IgnoreCase);
    public static string? Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "empty statement";
        if (sql.Contains(';')) return "one statement only, no semicolons";
        if (!Regex.IsMatch(sql, @"^\s*(select|with)\b", RegexOptions.IgnoreCase)) return "must start with SELECT or WITH";
        if (Forbidden.IsMatch(sql)) return "read-only: statement contains a forbidden keyword";
        if (sql.Contains("--") || sql.Contains("/*")) return "comments not allowed";
        // every FROM/JOIN target must be a callprep.* view, a CTE, or a subquery
        var ctes = new HashSet<string>(Regex.Matches(sql, @"(?:with|,)\s*([a-z_][a-z0-9_]*)\s+as\s*\(", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLower()));
        foreach (Match m in Regex.Matches(sql, @"\b(?:from|join)\s+([a-z_][a-z0-9_.]*)", RegexOptions.IgnoreCase))
        {
            var t = m.Groups[1].Value.ToLower();
            if (t.StartsWith("callprep.") || ctes.Contains(t)) continue;
            return $"'{t}' is not allowed; only callprep.* views (or CTEs) may be queried";
        }
        return null;
    }
}

sealed class Agent(AnthropicClient client, Tools tools, Db db)
{
    readonly ConcurrentDictionary<string, List<MessageParam>> sessions = new();
    string ModelId => Environment.GetEnvironmentVariable("CALLPREP_MODEL") ?? "claude-opus-5";

    static string SystemPrompt(AccessUser u) => $"""
        You are the call-prep assistant for Raritan Group's sales team. Today is {DateTime.Now:yyyy-MM-dd}.
        You answer questions about customers using the tools, which read a 15-minute-fresh copy of P21 sales history (invoices since 2022-03-30), open quotes, open orders, and cancelled quotes. Dollar figures are sell price. Cost and margin are not available.

        Signed-in user: {u.DisplayName ?? u.Login} ({u.Login}), role {u.Role}. Data visible to them: {u.ScopeText}.
        {(u.SeesAll ? "They can see every account." : "The database only returns customers assigned to this rep. If find_customer returns nothing for a name, say the account is not in their book (it may belong to another rep) and do not imply the company does not exist. Lookalike peers may be other reps' accounts; their names are shown but their sales figures are withheld, which is expected.")}

        How to work:
        - When a company is named, call find_customer first, then use the customer_id. If several match, pick the one with the most recent sales and say which you picked.
        - "What is X not buying that similar customers are" = peer_gap. Peers are the customers whose purchase mix looks most like this one (behavior, not the class label). Name two or three of the lookalikes so the rep can judge the comparison ("compared against 40 shops like Bennett Brothers Mechanical and L&L Mechanical"). Use class_gap only if asked for the market-class view, and caveat that the class label mixes trades.
        - Prefer the named tools. Use run_select only when they cannot answer the question.
        - Be fast and concrete. A rep reads this in the minute before a call. Lead with the answer, then 3 to 6 short bullets with dollar figures and dates. Name product groups the way the data names them. Do not pad, do not restate the question.
        - Judgment: the data shows what a customer buys, not what kind of contractor they are. If a gap looks like something they would never buy (an underground pipe group for a mechanical contractor, for example), say that as a caveat rather than pitching it.
        - P21 never closes quotes, so "open quotes" includes years-old dead ones. Treat quotes older than 6 months as dead unless the user asks about them, and never quote a lifetime open-quote total as if it were live pipeline.
        - If the data is thin or the class is missing, say so in one line and give the best answer available.
        """;

    // Conversation history is keyed by login + session id, so one person can never continue another person's session.
    public void Reset(AccessUser u, string session) => sessions.TryRemove($"{u.Login}:{session}", out _);

    public async IAsyncEnumerable<object> Run(string session, AccessUser me, string question, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var user = me.Login;
        var total = Stopwatch.StartNew();
        var history = sessions.GetOrAdd($"{user}:{session}", _ => new List<MessageParam>());
        history.Add(new() { Role = Role.User, Content = question });
        await db.Audit(user, session, "question", new { question, role = me.Role, scope = me.ScopeText });

        var sb = new StringBuilder();
        int rounds = 0; long inTok = 0, outTok = 0;
        while (rounds++ < 10)
        {
            Message? resp = null; string? apiError = null;
            try
            {
                resp = await client.Messages.Create(new MessageCreateParams
                {
                    Model = ModelId,
                    MaxTokens = 4000,
                    OutputConfig = new() { Effort = Effort.Medium },
                    System = SystemPrompt(me),
                    Tools = Tools.Definitions.Select(t => (ToolUnion)t).ToList(),
                    Messages = history,
                }, cancellationToken: ct);
            }
            catch (Exception ex) { apiError = ex.Message; }
            if (resp is null)
            {
                await db.Audit(user, session, "error", new { error = apiError }, total.ElapsedMilliseconds);
                yield return new { type = "error", text = apiError };
                yield break;
            }
            inTok += resp.Usage.InputTokens; outTok += resp.Usage.OutputTokens;

            var assistant = new List<ContentBlockParam>();
            var results = new List<ContentBlockParam>();
            var pendingEvents = new List<object>();
            foreach (var block in resp.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistant.Add(new TextBlockParam { Text = text.Text });
                    sb.Append(text.Text);
                    pendingEvents.Add(new { type = "text", text = text.Text });
                }
                else if (block.TryPickThinking(out var th))
                    assistant.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rth))
                    assistant.Add(new RedactedThinkingBlockParam { Data = rth.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    var input = JsonSerializer.SerializeToElement(tu.Input);
                    string result; string? sql; int rows; long ms;
                    try { (result, sql, rows, ms) = await tools.Invoke(tu.Name, input); }
                    catch (Exception ex)
                    {
                        // a failing tool must not kill the answer: hand the error to the model and log it
                        (result, sql, rows, ms) = (JsonSerializer.Serialize(new { error = ex is PostgresException px ? px.MessageText : ex.Message }), null, 0, 0);
                        await db.Audit(user, session, "error", new { tool = tu.Name, input, error = ex.Message });
                    }
                    await db.Audit(user, session, "tool", new { tool = tu.Name, input, sql }, ms, rows);
                    pendingEvents.Add(new { type = "tool", name = tu.Name, rows, ms });
                    results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = result });
                }
            }
            history.Add(new() { Role = Role.Assistant, Content = assistant });
            foreach (var ev in pendingEvents) yield return ev;

            if (resp.StopReason?.ToString()?.Contains("tool_use", StringComparison.OrdinalIgnoreCase) == true && results.Count > 0)
            {
                history.Add(new() { Role = Role.User, Content = results });
                continue;
            }
            if (resp.StopReason?.ToString()?.Contains("refusal", StringComparison.OrdinalIgnoreCase) == true)
                yield return new { type = "text", text = "\n(The model declined to answer this request.)" };
            break;
        }
        total.Stop();
        await db.Audit(user, session, "answer", new { answer = sb.ToString(), rounds, input_tokens = inTok, output_tokens = outTok }, total.ElapsedMilliseconds);
        yield return new { type = "done", ms = total.ElapsedMilliseconds, input_tokens = inTok, output_tokens = outTok, session_id = session };
    }
}
