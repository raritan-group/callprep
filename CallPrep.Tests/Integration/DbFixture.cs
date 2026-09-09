using Npgsql;

namespace CallPrep.Tests.Integration;

/// Real reportsdb through the SSH tunnel, as role callprep_ro, exactly like the API. One data source per test class.
public sealed class DbFixture : IDisposable
{
    internal NpgsqlDataSource Ds { get; }
    internal Db Db { get; }
    internal Tools Tools { get; }

    public DbFixture()
    {
        var pw = Environment.GetEnvironmentVariable("PG_PASSWORD_CALLPREP");
        Ds = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port={TestEnv.PgPort};Database=reportsdb;Username=callprep_ro;Password={pw};Timeout=10;CommandTimeout=35;Pooling=true;Maximum Pool Size=4").Build();
        Db = new Db(Ds);
        Tools = new Tools(Db);
    }

    public void Dispose() => Ds.Dispose();
}
