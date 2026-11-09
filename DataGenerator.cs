using Npgsql;

namespace storeBenchmark;
public static class DataGenerator
{
    public static async Task SeedAsync(int receipts)
    {
        await using var ds = Settings.CreatePgDataSource();
        await using var cmd = ds.CreateCommand("CALL sp_seed_benchmark_data(@n)");
        cmd.CommandTimeout = 900;
        cmd.Parameters.AddWithValue("n", receipts);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ClearAsync()
    {
        await using var ds = Settings.CreatePgDataSource();
        await using var cmd = ds.CreateCommand("CALL sp_clear_benchmark_data()");
        cmd.CommandTimeout = 900;
        await cmd.ExecuteNonQueryAsync();
    }
}
