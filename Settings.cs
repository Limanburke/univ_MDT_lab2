using Neo4j.Driver;
using Npgsql;

namespace storeBenchmark;
public static class Settings
{
    public static string PgConnectionString { get; } = BuildPg();

    private static string BuildPg()
    {
        var raw = Environment.GetEnvironmentVariable("STORE_PG") 
            ?? "Host=localhost;Port=5435;Database=store_bench;Username=postgres;Password=postgres";
        var b = new NpgsqlConnectionStringBuilder(raw)
        {
            MaxAutoPrepare = 20,
            AutoPrepareMinUsages = 1
        };
        return b.ConnectionString;
    }

    private static readonly Lazy<IDriver> _neo = new(() => GraphDatabase.Driver(
        Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687",
        AuthTokens.Basic(
            Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j",
            Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "password123")));

    public static IDriver Neo4jDriver => _neo.Value;

    public static async Task DisposeNeo4jAsync()
    {
        if (_neo.IsValueCreated) await _neo.Value.DisposeAsync();
    }

    public static NpgsqlDataSource CreatePgDataSource() => NpgsqlDataSource.Create(PgConnectionString);
}
