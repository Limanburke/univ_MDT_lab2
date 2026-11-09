using LiteDB;

namespace storeBenchmark;

public static class LiteDbReader
{
    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "offline_receipts.db");
        if (!File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            Console.WriteLine("Run the LiteDB demo first (menu item 5 or: dotnet run -- litedb).");
            return;
        }

        Console.WriteLine($"Reading: {path} ({new FileInfo(path).Length / 1024} KB)");

        using var db = new LiteDatabase($"Filename={path};ReadOnly=true");

        Console.WriteLine("\n[Collections] " + string.Join(", ", db.GetCollectionNames()));

        var receipts = db.GetCollection<OfflineReceipt>("receipts");
        Console.WriteLine($"[Total receipts] {receipts.Count()}");
        Console.WriteLine($"[Unsynced]       {receipts.Count(x => x.IsSynced == false)}");
        Console.WriteLine($"[Synced]         {receipts.Count(x => x.IsSynced == true)}");

        Console.WriteLine("\n--- Receipts (typed read) ---");
        foreach (var r in receipts.FindAll().OrderBy(x => x.Id))
        {
            decimal total = r.Items.Sum(i => i.Quantity * i.UnitPrice);
            Console.WriteLine($"#{r.Id} | dept {r.DepartmentId} | {r.Cashier} | {r.CreatedAt:yyyy-MM-dd HH:mm} | synced: {r.IsSynced} | total: {total}");
            foreach (var item in r.Items)
            {
                Console.WriteLine($"      {item.Sku,-12} {item.Name,-30} x{item.Quantity} @ {item.UnitPrice}");
            }
        }

        Console.WriteLine("\n--- Raw BSON documents (schema-less read) ---");
        var raw = db.GetCollection("receipts");
        foreach (var doc in raw.FindAll())
        {
            Console.WriteLine(JsonSerializer.Serialize(doc));
        }
    }
}