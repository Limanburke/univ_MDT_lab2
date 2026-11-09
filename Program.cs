using System.Globalization;
using storeBenchmark;

namespace storeBenchmark;

public class Program
{
    public static async Task Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length > 0)
        {
            string cmd = args[0].ToLower();

            if (cmd == "full")
                await RunFullAsync();
            else if (cmd == "litedb")
                LiteDbDemo.Run();
            else if (cmd == "litedb-read")
                LiteDbReader.Run();
            else
                Console.WriteLine("Unknown command. Use: full, litedb");

            await Settings.DisposeNeo4jAsync();
            return;
        }

        while (true)
        {
            Console.WriteLine("\n=== Store Benchmark Menu ===");
            Console.WriteLine("1. Seed PostgreSQL data");
            Console.WriteLine("2. Run ETL to Neo4j");
            Console.WriteLine("3. Run benchmark (current data)");
            Console.WriteLine("4. Run full test (1k, 10k, 100k)");
            Console.WriteLine("5. LiteDB demo");
            Console.WriteLine("6. Clear test data");
            Console.WriteLine("7. Read LiteDB file");
            Console.WriteLine("0. Exit");
            Console.Write("Choice: ");

            string choice = Console.ReadLine() ?? "";

            try
            {
                switch (choice)
                {
                    case "1":
                        Console.Write("Receipts count (e.g. 10000): ");
                        if (int.TryParse(Console.ReadLine(), out int count))
                        {
                            await DataGenerator.SeedAsync(count);
                            Console.WriteLine("Done");
                        }
                        break;

                    case "2":
                        await Neo4jEtl.RunAsync();
                        break;

                    case "3":
                        var results = await Benchmark.RunAsync(0);
                        Benchmark.PrintTable(results);
                        break;

                    case "4":
                        await RunFullAsync();
                        break;

                    case "5":
                        LiteDbDemo.Run();
                        break;

                    case "6":
                        await ClearDataAsync();
                        Console.WriteLine("Data cleared");
                        break;

                    case "7":
                        LiteDbReader.Run();
                        break;

                    case "0":
                        await Settings.DisposeNeo4jAsync();
                        return;

                    default:
                        Console.WriteLine("Wrong choice");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
        }
    }

    private static async Task RunFullAsync()
    {
        int[] scales = { 1000, 10000, 100000 };
        var allResults = new List<BenchRow>();

        foreach (int scale in scales)
        {
            Console.WriteLine($"\nTesting scale: {scale} receipts...");
            await DataGenerator.SeedAsync(scale);
            await Neo4jEtl.RunAsync();

            var rows = await Benchmark.RunAsync(scale);
            allResults.AddRange(rows);
        }

        Console.WriteLine("\nFinal Results:");
        Benchmark.PrintTable(allResults);
        Benchmark.SaveCsv(allResults, "benchmark_results.csv");
    }

    private static async Task ClearDataAsync()
    {
        await DataGenerator.ClearAsync();

        await using var session = Settings.Neo4jDriver.AsyncSession();
        var cursor = await session.RunAsync("MATCH (n) CALL { WITH n DETACH DELETE n } IN TRANSACTIONS OF 10000 ROWS");
        await cursor.ConsumeAsync();
    }
}