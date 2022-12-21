using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ModelScanner.Database.Entities;

namespace ModelScanner.Database;

public class CivitaiDbContext : DbContext
{
    public CivitaiDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<ModelFile> ModelFiles => Set<ModelFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelFile>()
            .HasNoKey()
            .ToTable("ModelFile")
            .Property(x => x.Url)
            .HasColumnName("url");
    }
}
