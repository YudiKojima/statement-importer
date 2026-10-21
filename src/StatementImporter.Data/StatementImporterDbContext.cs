using Microsoft.EntityFrameworkCore;
using StatementImporter.Data.Entities;

namespace StatementImporter.Data;

public sealed class StatementImporterDbContext(DbContextOptions<StatementImporterDbContext> options) : DbContext(options)
{
    public const int MaxRawLineLength = 500;

    public DbSet<Import> Imports => Set<Import>();
    public DbSet<ImportChunk> ImportChunks => Set<ImportChunk>();
    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();
    public DbSet<ImportError> ImportErrors => Set<ImportError>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Import>(e =>
        {
            e.ToTable("imports");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.FileHash).HasMaxLength(64).IsFixedLength();
            e.HasIndex(x => x.FileHash).IsUnique();
            e.Property(x => x.FilePath).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<ImportChunk>(e =>
        {
            e.ToTable("import_chunks");
            e.HasIndex(x => new { x.ImportId, x.ChunkIndex }).IsUnique();
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });

        modelBuilder.Entity<BankTransaction>(e =>
        {
            e.ToTable("transactions");
            e.Property(x => x.TransactionId).HasMaxLength(64);
            e.HasIndex(x => x.TransactionId).IsUnique();
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.ImportId, x.LineNumber });
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });

        modelBuilder.Entity<ImportError>(e =>
        {
            e.ToTable("import_errors");
            e.Property(x => x.RawLine).HasMaxLength(MaxRawLineLength);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.HasIndex(x => new { x.ImportId, x.LineNumber });
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });
    }
}
