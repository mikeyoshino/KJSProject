using Microsoft.EntityFrameworkCore;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DownloadEntry> DownloadEntries => Set<DownloadEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DownloadEntry>();
        entity.HasIndex(e => e.RapidgatorUrl).HasDatabaseName("ix_rapidgator_url");
        entity.HasIndex(e => new { e.Status, e.ExpiresAt }).HasDatabaseName("ix_status_expires");
        entity.HasIndex(e => e.LastAccessedAt).HasDatabaseName("ix_last_accessed");
        entity.Property(e => e.Status).HasConversion<string>();
    }
}
