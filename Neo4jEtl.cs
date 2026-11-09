using System.Diagnostics;
using Neo4j.Driver;
using Npgsql;

namespace storeBenchmark;

public static class Neo4jEtl
{
    private const int BatchSize = 5000;

    public static async Task<double> RunAsync()
    {
        var timer = Stopwatch.StartNew();
        await using var session = Settings.Neo4jDriver.AsyncSession();
        await using var ds = Settings.CreatePgDataSource();

        Console.WriteLine("ETL: Resetting Neo4j graph and constraints...");
        string[] labels = { "Category", "Product", "Customer", "Receipt" };
        foreach (var label in labels)
        {
            await ExecAsync(session, $"CREATE CONSTRAINT IF NOT EXISTS FOR (n:{label}) REQUIRE n.id IS UNIQUE");
        }
        await ExecAsync(session, "MATCH (n) CALL { WITH n DETACH DELETE n } IN TRANSACTIONS OF 10000 ROWS");

        var rows = new List<Dictionary<string, object?>>();
        await using (var cmd = ds.CreateCommand("SELECT id, name FROM category"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = (long)r.GetInt32(0),
                    ["name"] = r.GetString(1)
                });
            }
        }
        await ExecAsync(session, "UNWIND $rows AS row CREATE (:Category {id: row.id, name: row.name})", rows);
        Console.WriteLine($"Imported categories: {rows.Count}");

        long productsCount = 0;
        rows.Clear();
        await using (var cmd = ds.CreateCommand("SELECT id, sku, name, price, category_id FROM product"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = (long)r.GetInt32(0),
                    ["sku"] = r.GetString(1),
                    ["name"] = r.GetString(2),
                    ["price"] = (double)r.GetDecimal(3),
                    ["cid"] = (long)r.GetInt32(4)
                });

                if (rows.Count >= BatchSize)
                {
                    productsCount += await FlushProductsAsync(session, rows);
                }
            }
        }
        if (rows.Count > 0)
        {
            productsCount += await FlushProductsAsync(session, rows);
        }
        Console.WriteLine($"Imported products: {productsCount}");

        long customersCount = 0;
        rows.Clear();
        await using (var cmd = ds.CreateCommand("SELECT id FROM customer"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = (long)r.GetInt32(0)
                });

                if (rows.Count >= BatchSize)
                {
                    await ExecAsync(session, "UNWIND $rows AS row CREATE (:Customer {id: row.id})", rows);
                    customersCount += rows.Count;
                    rows.Clear();
                }
            }
        }
        if (rows.Count > 0)
        {
            await ExecAsync(session, "UNWIND $rows AS row CREATE (:Customer {id: row.id})", rows);
            customersCount += rows.Count;
        }
        Console.WriteLine($"Imported customers: {customersCount}");

        long receiptsCount = 0;
        rows.Clear();
        await using (var cmd = ds.CreateCommand("SELECT id, customer_id FROM sales_transaction WHERE status = 'Completed' ORDER BY id"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = (long)r.GetInt32(0),
                    ["cid"] = r.IsDBNull(1) ? (long?)null : r.GetInt32(1)
                });

                if (rows.Count >= BatchSize)
                {
                    receiptsCount += await FlushReceiptsAsync(session, rows);
                }
            }
        }
        if (rows.Count > 0)
        {
            receiptsCount += await FlushReceiptsAsync(session, rows);
        }
        Console.WriteLine($"Imported receipts: {receiptsCount}");

        long itemsCount = 0;
        rows.Clear();
        await using (var cmd = ds.CreateCommand(@"
            SELECT si.transaction_id, si.product_id, si.quantity, si.unit_price
            FROM sales_item si
            JOIN sales_transaction st ON st.id = si.transaction_id
            WHERE st.status = 'Completed'
            ORDER BY si.transaction_id"))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["tx"] = (long)r.GetInt32(0),
                    ["pid"] = (long)r.GetInt32(1),
                    ["qty"] = (long)r.GetInt32(2),
                    ["price"] = (double)r.GetDecimal(3)
                });

                if (rows.Count >= BatchSize)
                {
                    itemsCount += await FlushItemsAsync(session, rows);
                }
            }
        }
        if (rows.Count > 0)
        {
            itemsCount += await FlushItemsAsync(session, rows);
        }
        Console.WriteLine($"Imported items: {itemsCount}");

        timer.Stop();
        Console.WriteLine($"ETL finished in {timer.Elapsed.TotalSeconds:F1} s");

        await VerifyCountsAsync(session, ds);
        return timer.Elapsed.TotalMilliseconds;
    }

    private static async Task<long> FlushProductsAsync(IAsyncSession s, List<Dictionary<string, object?>> rows)
    {
        long count = rows.Count;
        await ExecAsync(s, @"
            UNWIND $rows AS row
            MATCH (c:Category {id: row.cid})
            CREATE (p:Product {id: row.id, sku: row.sku, name: row.name, price: row.price})-[:IN_CATEGORY]->(c)",
            rows);
        rows.Clear();
        return count;
    }

    private static async Task<long> FlushReceiptsAsync(IAsyncSession s, List<Dictionary<string, object?>> rows)
    {
        long count = rows.Count;
        await ExecAsync(s, @"
            UNWIND $rows AS row
            CREATE (r:Receipt {id: row.id})
            WITH r, row
            WHERE row.cid IS NOT NULL
            MATCH (c:Customer {id: row.cid})
            CREATE (c)-[:MADE]->(r)",
            rows);
        rows.Clear();
        return count;
    }

    private static async Task<long> FlushItemsAsync(IAsyncSession s, List<Dictionary<string, object?>> rows)
    {
        long count = rows.Count;
        await ExecAsync(s, @"
            UNWIND $rows AS row
            MATCH (r:Receipt {id: row.tx})
            MATCH (p:Product {id: row.pid})
            CREATE (r)-[:CONTAINS {qty: row.qty, price: row.price}]->(p)",
            rows);
        rows.Clear();
        return count;
    }

    private static async Task VerifyCountsAsync(IAsyncSession s, NpgsqlDataSource ds)
    {
        Console.WriteLine("Verifying record counts (PostgreSQL vs Neo4j):");
        await CheckCountAsync(s, ds, "Category",
            "SELECT count(*) FROM category",
            "MATCH (n:Category) RETURN count(n) AS c");

        await CheckCountAsync(s, ds, "Product",
            "SELECT count(*) FROM product",
            "MATCH (n:Product) RETURN count(n) AS c");

        await CheckCountAsync(s, ds, "Customer",
            "SELECT count(*) FROM customer",
            "MATCH (n:Customer) RETURN count(n) AS c");

        await CheckCountAsync(s, ds, "Receipt",
            "SELECT count(*) FROM sales_transaction WHERE status = 'Completed'",
            "MATCH (n:Receipt) RETURN count(n) AS c");

        await CheckCountAsync(s, ds, "CONTAINS",
            "SELECT count(*) FROM sales_item si JOIN sales_transaction st ON st.id = si.transaction_id WHERE st.status = 'Completed'",
            "MATCH (:Receipt)-[r:CONTAINS]->(:Product) RETURN count(r) AS c");

        await CheckCountAsync(s, ds, "MADE",
            "SELECT count(*) FROM sales_transaction WHERE status = 'Completed' AND customer_id IS NOT NULL",
            "MATCH (:Customer)-[r:MADE]->(:Receipt) RETURN count(r) AS c");
    }

    private static async Task CheckCountAsync(IAsyncSession s, NpgsqlDataSource ds, string entity, string sql, string cypher)
    {
        await using var cmd = ds.CreateCommand(sql);
        long pg = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        var cursor = await s.RunAsync(cypher);
        long neo = 0;
        if (await cursor.FetchAsync())
        {
            neo = cursor.Current["c"].As<long>();
        }

        string status = (pg == neo) ? "OK" : "MISMATCH";
        Console.WriteLine($"  {entity,-10} PG: {pg,-8} Neo4j: {neo,-8} [{status}]");
    }

    private static async Task ExecAsync(IAsyncSession s, string cypher, List<Dictionary<string, object?>>? rows = null)
    {
        if (rows == null)
        {
            var cursor = await s.RunAsync(cypher);
            await cursor.ConsumeAsync();
        }
        else
        {
            var parameters = new Dictionary<string, object>
            {
                ["rows"] = rows.ToList()
            };
            var cursor = await s.RunAsync(cypher, parameters);
            await cursor.ConsumeAsync();
        }
    }
}