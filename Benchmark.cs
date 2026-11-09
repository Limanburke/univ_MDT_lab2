using System.Diagnostics;
using System.Text;
using Neo4j.Driver;
using Npgsql;

namespace storeBenchmark;

public sealed record Scenario(string Name, string Sql, string Cypher, bool UsesCategory);

public sealed record TestProduct(string Class, int Id, int ReceiptCount);

public sealed record BenchRow(
    int Scale, string Scenario, string ProductClass, int ProductId, int ReceiptsWithProduct,
    double PgMedian, double PgP95, double NeoMedian, double NeoP95, bool ResultsEqual);

public static class Benchmark
{
    private const int Warmup = 5;
    private const int Iterations = 30;

    public static readonly Scenario[] Scenarios =
    {
        new("1. Co-purchase (1 hop)",
            @"SELECT si2.product_id, COUNT(*) AS cnt
              FROM sales_item si1
              JOIN sales_transaction st ON st.id = si1.transaction_id AND st.status = 'Completed'
              JOIN sales_item si2 ON si2.transaction_id = si1.transaction_id AND si2.product_id <> si1.product_id
              WHERE si1.product_id = @pid
              GROUP BY si2.product_id
              ORDER BY cnt DESC, si2.product_id
              LIMIT 5",
            @"MATCH (p:Product {id: $pid})<-[:CONTAINS]-(r:Receipt)-[:CONTAINS]->(rec:Product)
              WHERE rec <> p
              RETURN rec.id AS product_id, count(r) AS cnt
              ORDER BY cnt DESC, product_id ASC
              LIMIT 5",
            false),

        new("2. Co-purchase with category",
            @"SELECT si2.product_id, COUNT(*) AS cnt
              FROM sales_item si1
              JOIN sales_transaction st ON st.id = si1.transaction_id AND st.status = 'Completed'
              JOIN sales_item si2 ON si2.transaction_id = si1.transaction_id AND si2.product_id <> si1.product_id
              JOIN product p2 ON p2.id = si2.product_id
              WHERE si1.product_id = @pid AND p2.category_id = @cat
              GROUP BY si2.product_id
              ORDER BY cnt DESC, si2.product_id
              LIMIT 5",
            @"MATCH (p:Product {id: $pid})<-[:CONTAINS]-(r:Receipt)-[:CONTAINS]->(rec:Product)-[:IN_CATEGORY]->(:Category {id: $cat})
              WHERE rec <> p
              RETURN rec.id AS product_id, count(r) AS cnt
              ORDER BY cnt DESC, product_id ASC
              LIMIT 5",
            true),

        new("3. Customers (2 hops)",
            @"SELECT t.product_id, COUNT(DISTINCT st1.customer_id) AS cnt
              FROM sales_item b
              JOIN sales_transaction st1 ON st1.id = b.transaction_id
                                        AND st1.status = 'Completed' AND st1.customer_id IS NOT NULL
              JOIN sales_transaction st2 ON st2.customer_id = st1.customer_id
                                        AND st2.id <> st1.id AND st2.status = 'Completed'
              JOIN sales_item t ON t.transaction_id = st2.id AND t.product_id <> b.product_id
              WHERE b.product_id = @pid
              GROUP BY t.product_id
              ORDER BY cnt DESC, t.product_id
              LIMIT 5",
            @"MATCH (p:Product {id: $pid})<-[:CONTAINS]-(r1:Receipt)<-[:MADE]-(c:Customer)-[:MADE]->(r2:Receipt)-[:CONTAINS]->(rec:Product)
              WHERE rec <> p AND r1 <> r2
              RETURN rec.id AS product_id, count(DISTINCT c) AS cnt
              ORDER BY cnt DESC, product_id ASC
              LIMIT 5",
            false),
    };

    public static async Task<(List<TestProduct> products, int categoryId)> PickTestDataAsync(NpgsqlDataSource ds)
    {
        var ranked = new List<(int id, int cnt)>();
        await using (var cmd = ds.CreateCommand(@"
            SELECT si.product_id, COUNT(DISTINCT si.transaction_id) AS cnt
            FROM sales_item si
            JOIN sales_transaction st ON st.id = si.transaction_id AND st.status = 'Completed'
            GROUP BY si.product_id
            ORDER BY cnt DESC, si.product_id"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                ranked.Add((r.GetInt32(0), (int)r.GetInt64(1)));
            }
        }

        if (ranked.Count == 0)
        {
            throw new Exception("No sales data found. Please run seed procedure first.");
        }

        (string, int)[] picks =
        {
            ("hit (rank 1)", 1),
            ("medium (rank 100)", 100),
            ("rare (rank 1000)", 1000)
        };

        var result = new List<TestProduct>();
        foreach (var (label, rank) in picks)
        {
            int index = Math.Min(rank, ranked.Count) - 1;
            var item = ranked[index];

            if (!result.Any(x => x.Id == item.id))
            {
                result.Add(new TestProduct(label, item.id, item.cnt));
            }
        }

        await using var catCmd = ds.CreateCommand(@"
            SELECT p.category_id
            FROM sales_item si
            JOIN product p ON p.id = si.product_id
            GROUP BY p.category_id
            ORDER BY COUNT(*) DESC, p.category_id
            LIMIT 1");

        int categoryId = Convert.ToInt32(await catCmd.ExecuteScalarAsync());
        return (result, categoryId);
    }

    public static async Task<List<BenchRow>> RunAsync(int scale)
    {
        await using var ds = Settings.CreatePgDataSource();
        var (products, categoryId) = await PickTestDataAsync(ds);

        if (scale <= 0)
        {
            await using var cntCmd = ds.CreateCommand("SELECT count(*) FROM sales_transaction WHERE status = 'Completed'");
            scale = Convert.ToInt32(await cntCmd.ExecuteScalarAsync());
        }

        Console.WriteLine($"\nBenchmark: scale = {scale} receipts, category id = {categoryId}");
        foreach (var p in products)
        {
            Console.WriteLine($"  Product: {p.Class,-20} id={p.Id}, receipts: {p.ReceiptCount}");
        }

        await using var pgConn = await ds.OpenConnectionAsync();
        await using var neo = Settings.Neo4jDriver.AsyncSession();

        var rows = new List<BenchRow>();
        foreach (var sc in Scenarios)
        {
            foreach (var p in products)
            {
                Console.Write($"  {sc.Name} | {p.Class} ... ");

                var (pgRes, pgFirst) = await RunPgAsync(pgConn, sc, p.Id, categoryId);
                var (neoRes, neoFirst) = await RunNeoAsync(neo, sc, p.Id, categoryId);
                bool equal = pgRes.SequenceEqual(neoRes);

                var pgTimes = await MeasureAsync(() => RunPgAsync(pgConn, sc, p.Id, categoryId), pgFirst);
                var neoTimes = await MeasureAsync(() => RunNeoAsync(neo, sc, p.Id, categoryId), neoFirst);

                var row = new BenchRow(
                    scale, sc.Name, p.Class, p.Id, p.ReceiptCount,
                    GetPercentile(pgTimes, 0.5), GetPercentile(pgTimes, 0.95),
                    GetPercentile(neoTimes, 0.5), GetPercentile(neoTimes, 0.95),
                    equal);

                rows.Add(row);

                string status = equal ? "OK" : "MISMATCH";
                Console.WriteLine($"PG: {row.PgMedian:F2} ms, Neo4j: {row.NeoMedian:F2} ms [{status}]");

                if (!equal)
                {
                    Console.WriteLine($"    PG   : {string.Join(", ", pgRes)}");
                    Console.WriteLine($"    Neo4j: {string.Join(", ", neoRes)}");
                }
            }
        }
        return rows;
    }

    private static async Task<List<double>> MeasureAsync(Func<Task<(List<(long, long)>, double)>> action, double firstMs)
    {
        int warmup = firstMs > 500 ? 1 : Warmup;
        int iterations = firstMs > 500 ? 8 : Iterations;

        for (int i = 0; i < warmup; i++)
        {
            await action();
        }

        var times = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var res = await action();
            times.Add(res.Item2);
        }
        return times;
    }

    private static async Task<(List<(long, long)> rows, double ms)> RunPgAsync(
        NpgsqlConnection conn, Scenario sc, int pid, int cat)
    {
        var timer = Stopwatch.StartNew();
        await using var cmd = new NpgsqlCommand(sc.Sql, conn);
        cmd.Parameters.AddWithValue("pid", pid);
        if (sc.UsesCategory)
        {
            cmd.Parameters.AddWithValue("cat", cat);
        }

        var rows = new List<(long, long)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }

        timer.Stop();
        return (rows, timer.Elapsed.TotalMilliseconds);
    }

    private static async Task<(List<(long, long)> rows, double ms)> RunNeoAsync(
        IAsyncSession session, Scenario sc, int pid, int cat)
    {
        var timer = Stopwatch.StartNew();
        var parameters = new Dictionary<string, object>
        {
            ["pid"] = (long)pid,
            ["cat"] = (long)cat
        };

        var cursor = await session.RunAsync(sc.Cypher, parameters);
        var rows = new List<(long, long)>();

        while (await cursor.FetchAsync())
        {
            var rec = cursor.Current;
            rows.Add((rec["product_id"].As<long>(), rec["cnt"].As<long>()));
        }

        timer.Stop();
        return (rows, timer.Elapsed.TotalMilliseconds);
    }

    private static double GetPercentile(List<double> samples, double p)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Count) index = sorted.Count - 1;
        return sorted[index];
    }

    public static void PrintTable(IEnumerable<BenchRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Scale | Scenario | Product (receipts) | PostgreSQL med/p95 (ms) | Neo4j med/p95 (ms) | Speedup | Result |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|:---:|");

        foreach (var r in rows)
        {
            double ratio = r.PgMedian / Math.Max(0.001, r.NeoMedian);
            string match = r.ResultsEqual ? "OK" : "MISMATCH";
            sb.AppendLine($"| {r.Scale} | {r.Scenario} | {r.ProductClass} ({r.ReceiptsWithProduct}) | {r.PgMedian:F2} / {r.PgP95:F2} | {r.NeoMedian:F2} / {r.NeoP95:F2} | {ratio:F2}x | {match} |");
        }

        Console.WriteLine(sb.ToString());
    }

    public static void SaveCsv(IEnumerable<BenchRow> rows, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scale,scenario,product_class,product_id,receipts_with_product,pg_median_ms,pg_p95_ms,neo_median_ms,neo_p95_ms,results_equal");

        foreach (var r in rows)
        {
            sb.AppendLine($"{r.Scale},\"{r.Scenario}\",\"{r.ProductClass}\",{r.ProductId},{r.ReceiptsWithProduct},{r.PgMedian:F3},{r.PgP95:F3},{r.NeoMedian:F3},{r.NeoP95:F3},{r.ResultsEqual}");
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"CSV saved: {path}");
    }
}