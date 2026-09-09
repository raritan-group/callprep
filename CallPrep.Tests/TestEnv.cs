// Test environment bootstrap. Runs once when the test assembly loads, before any WebApplicationFactory builds Program.cs.
//
//   * Secrets (ANTHROPIC_API_KEY, PG_PASSWORD_CALLPREP) come from the process environment when tests/run-tests.ps1 set them,
//     otherwise from the DPAPI secrets store through the same python loader run-local.ps1 uses. Nothing is written to disk.
//   * Entra ids are the public identifiers from the README; the client secret is a dummy unless the environment already has it.
//     The HTTP tests sign in through a test scheme, so the OIDC handler is only exercised for the /signin redirect itself.
//   * Postgres is reached through the SSH tunnel on 127.0.0.1:15432 (run-local.ps1 / run-tests.ps1 bring it up).
//   * Tiers: unit tests need nothing; DB tests need the tunnel; live model tests need CALLPREP_LIVE=1 (they spend API credits).

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace CallPrep.Tests;

public static class TestEnv
{
    public const string AdminLogin = "it@raritangroup.com";          // enabled admin (seed 004)
    public const string RepLogin = "ddickman@raritangroup.com";      // enabled rep, salesrep 25505 (Doug Dickman)
    public const string DisabledLogin = "paul@raritangroup.com";     // seeded admin row, enabled = false at the time of writing
    public const string UnknownLogin = "nobody@raritangroup.com";    // no user_access row
    public const string TestSessionPrefix = "test-";                 // audit_log rows written by tests carry this session prefix

    public static string RepoRoot { get; } = FindRepoRoot();
    public static string PgPort => Environment.GetEnvironmentVariable("CALLPREP_PG_PORT") ?? "15432";
    public static bool Live => Environment.GetEnvironmentVariable("CALLPREP_LIVE") == "1";
    public static bool TunnelUp { get; private set; }
    public static string? SecretsError { get; private set; }

    /// Demo customers: CALLPREP_TEST_CUSTOMERS="name;name;..." overrides the built-in set.
    /// The default set covers every market class, the null-class case, the hero account (Buist) and both rep-owned and house accounts.
    public static IReadOnlyList<string> DemoCustomers { get; } =
        (Environment.GetEnvironmentVariable("CALLPREP_TEST_CUSTOMERS") is { Length: > 0 } s
            ? s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[]
            {
                "Buist",                       // CONTRACTOR, Doug Dickman — the demo hero account
                "Northeast Remsco",            // CONTRACTOR, largest
                "Coppola Services",            // CONTRACTOR
                "Hungerford & Terry",          // PROCESS
                "Middlesex Water",             // UTILITY, largest customer overall
                "IMTT-Bayonne",                // ENERGY
                "DCO Energy",                  // Engineering
                "Port Authority of NY",        // Transportation
                "W.W. Grainger",               // RESALE, house account
                "Bell Piping Solutions",       // NO market class (class_gap must degrade gracefully)
            }).ToList();

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("CALLPREP_PG_PORT", PgPort);
        Environment.SetEnvironmentVariable("CALLPREP_BASE_URL", Environment.GetEnvironmentVariable("CALLPREP_BASE_URL") ?? "http://localhost:5070");
        Environment.SetEnvironmentVariable("ENTRA_TENANT_ID", Environment.GetEnvironmentVariable("ENTRA_TENANT_ID") ?? "6ea34d6a-c05b-4142-89e3-160ce1904606");
        Environment.SetEnvironmentVariable("ENTRA_CLIENT_ID", Environment.GetEnvironmentVariable("ENTRA_CLIENT_ID") ?? "c69f5373-bbaa-4282-85b5-7a00c64aed26");
        Environment.SetEnvironmentVariable("ENTRA_CLIENT_SECRET", Environment.GetEnvironmentVariable("ENTRA_CLIENT_SECRET") ?? "test-not-a-real-secret");
        Environment.SetEnvironmentVariable("CALLPREP_WHISPER_MODEL", Environment.GetEnvironmentVariable("CALLPREP_WHISPER_MODEL") ?? Path.Combine(RepoRoot, "models", "ggml-base.en.bin"));
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("CALLPREP_WARM", "0");   // no background list warmer during tests

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PG_PASSWORD_CALLPREP")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            LoadSecretsFromStore();
        // Program.cs throws at startup without these; give the unit-only run a value so the assembly still loads
        Environment.SetEnvironmentVariable("PG_PASSWORD_CALLPREP", Environment.GetEnvironmentVariable("PG_PASSWORD_CALLPREP") ?? "unset");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "unset");

        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", int.Parse(PgPort));
            TunnelUp = true;
        }
        catch { TunnelUp = false; }
    }

    static void LoadSecretsFromStore()
    {
        try
        {
            const string py = """
                import sys; sys.path.insert(0, r"C:\Scripts\.secrets")
                import secrets_loader as S
                d = S.parse_env(S.decrypt_env_text())
                for k in ("ANTHROPIC_API_KEY", "PG_PASSWORD_CALLPREP"):
                    print(k + "=" + d[k])
                """;
            var psi = new ProcessStartInfo("python", "-") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(py); p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = line.IndexOf('=');
                if (i > 0) Environment.SetEnvironmentVariable(line[..i].Trim(), line[(i + 1)..].Trim());
            }
            if (p.ExitCode != 0) SecretsError = stderr.Trim();
        }
        catch (Exception ex) { SecretsError = ex.Message; }
    }

    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "run-local.ps1"))) d = d.Parent;
        return d?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static void RequireTunnel() => Skip.IfNot(TunnelUp, $"Postgres tunnel not listening on 127.0.0.1:{PgPort}. Run tests\\run-tests.ps1 (it opens the tunnel) or run-local.ps1 first.");
    public static void RequireLive() => Skip.IfNot(Live, "Set CALLPREP_LIVE=1 to run model-in-the-loop tests (they spend Anthropic credits).");
}
