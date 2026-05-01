using Microsoft.EntityFrameworkCore;
using OcrTradingBackend.Models;

namespace OcrTradingBackend.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<OcrZone> OcrZones => Set<OcrZone>();
    public DbSet<CoordinateCapture> CoordinateCaptures => Set<CoordinateCapture>();
    public DbSet<CityCapture> CityCaptures => Set<CityCapture>();
    public DbSet<PriceCapture> PriceCaptures => Set<PriceCapture>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OcrZone>().HasIndex(x => x.Name).IsUnique();
        b.Entity<AppSetting>().HasIndex(x => x.Key).IsUnique();
        b.Entity<CoordinateCapture>().HasIndex(x => x.CapturedAtUtc);
        b.Entity<CityCapture>().HasIndex(x => x.CapturedAtUtc);
        b.Entity<PriceCapture>().HasIndex(x => new { x.City, x.ItemName, x.TradeType, x.CapturedAtUtc });
    }
}
