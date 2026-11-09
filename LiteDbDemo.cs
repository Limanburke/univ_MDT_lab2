using LiteDB;

namespace storeBenchmark;

public class OfflineReceipt
{
    public int Id { get; set; }
    public int DepartmentId { get; set; }
    public string Cashier { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsSynced { get; set; }
    public List<OfflineItem> Items { get; set; } = new();
}

public class OfflineItem
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public static class LiteDbDemo
{
    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "offline_receipts.db");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var db = new LiteDatabase(path);
        var receipts = db.GetCollection<OfflineReceipt>("receipts");
        receipts.EnsureIndex(x => x.IsSynced);
        receipts.EnsureIndex(x => x.CreatedAt);

        Console.WriteLine($"Database path: {path}");

        var r1 = new OfflineReceipt
        {
            DepartmentId = 1,
            Cashier = "i_melnyk",
            CreatedAt = DateTime.UtcNow,
            Items = new List<OfflineItem>
            {
                new() { Sku = "TEL-SMP-001", Name = "Smartphone Pro Max 256GB", Quantity = 1, UnitPrice = 39999.00m },
                new() { Sku = "APP-MWO-201", Name = "Microwave Oven Digital 23L", Quantity = 2, UnitPrice = 4800.00m }
            }
        };

        var r2 = new OfflineReceipt
        {
            DepartmentId = 3,
            Cashier = "d_shevchenko",
            CreatedAt = DateTime.UtcNow,
            Items = new List<OfflineItem>
            {
                new() { Sku = "CMP-LAP-101", Name = "Business Laptop Ultra 15", Quantity = 1, UnitPrice = 42000.00m }
            }
        };

        var r3 = new OfflineReceipt
        {
            DepartmentId = 1,
            Cashier = "i_melnyk",
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            IsSynced = true,
            Items = new List<OfflineItem>
            {
                new() { Sku = "TEL-SMP-002", Name = "Smartphone Lite 128GB", Quantity = 1, UnitPrice = 14500.00m }
            }
        };

        receipts.Insert(r1);
        receipts.Insert(r2);
        receipts.Insert(r3);
        Console.WriteLine($"[1. INSERT] Saved 3 receipts. Total records: {receipts.Count()}");

        var doc = BsonMapper.Global.ToDocument(r1);
        Console.WriteLine("\n[BSON Document Structure]\n" + JsonSerializer.Serialize(doc));

        var byId = receipts.FindById(r1.Id);
        decimal totalAmount = byId.Items.Sum(i => i.Quantity * i.UnitPrice);
        Console.WriteLine($"\n[2a. READ - FindById] Receipt #{byId.Id}: items count = {byId.Items.Count}, total = {totalAmount}");

        var pending = receipts.Find(x => x.IsSynced == false).ToList();
        Console.WriteLine($"\n[2b. READ - Indexed Filter] Unsynced receipts: {pending.Count}");

        var withSku = receipts.Find(BsonExpression.Create("$.Items[*].Sku ANY = @0", new BsonValue("TEL-SMP-001"))).ToList();
        Console.WriteLine($"[2c. READ - Nested Array Search] Receipts containing SKU 'TEL-SMP-001': {withSku.Count}");

        foreach (var receipt in pending)
        {
            receipt.IsSynced = true;
            receipts.Update(receipt);
        }
        Console.WriteLine($"\n[3. UPDATE] Marked as synced: {pending.Count}. Remaining unsynced: {receipts.Count(x => x.IsSynced == false)}");

        var cutoffDate = DateTime.UtcNow.AddDays(-7);
        int deletedCount = receipts.DeleteMany(x => x.IsSynced == true && x.CreatedAt < cutoffDate);
        Console.WriteLine($"[4. DELETE] Deleted synced receipts older than 7 days: {deletedCount}. Remaining total: {receipts.Count()}");

        long fileSizeKb = new FileInfo(path).Length / 1024;
        Console.WriteLine($"\nDatabase file size: {fileSizeKb} KB");
    }
}