using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;

namespace AMRecomen.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<TrendMetric> TrendMetrics => Set<TrendMetric>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<User> Users => Set<User>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configuración de MediaItem
        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
            entity.Property(e => e.EnglishTitle).HasMaxLength(250);
            entity.Property(e => e.OriginalTitle).HasMaxLength(250);
            entity.Property(e => e.AlternativeTitles).HasMaxLength(2000);
            entity.Property(e => e.Synopsis).HasMaxLength(2000);
            entity.Property(e => e.CoverImageUrl).HasMaxLength(500);
            
            // Configurar Slug único para SEO
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(300);
            entity.HasIndex(e => e.Slug).IsUnique();
            
            // Indexar ExternalId y Tipo para búsquedas rápidas
            entity.HasIndex(e => e.ExternalId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.HypeScore);
        });

        // Configuración de TrendMetric
        modelBuilder.Entity<TrendMetric>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MetricType).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.RecordedAt);

            // Relación Uno a Muchos con MediaItem
            entity.HasOne(e => e.MediaItem)
                .WithMany(m => m.TrendMetrics)
                .HasForeignKey(e => e.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configuración de Tag
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.NormalizedName).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.NormalizedName).IsUnique();

            // Relación Muchos a Muchos con MediaItem
            entity.HasMany(e => e.MediaItems)
                .WithMany(m => m.Tags)
                .UsingEntity<Dictionary<string, object>>(
                    "MediaItemTag",
                    j => j.HasOne<MediaItem>().WithMany().HasForeignKey("MediaItemId").OnDelete(DeleteBehavior.Cascade),
                    j => j.HasOne<Tag>().WithMany().HasForeignKey("TagId").OnDelete(DeleteBehavior.Cascade)
                );
        });

        // Configuración de User
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Email).IsRequired().HasMaxLength(250);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(500);
        });

        // Configuración de LibraryItem
        modelBuilder.Entity<LibraryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Evitar duplicados (un usuario solo puede tener una obra una vez en su biblioteca)
            entity.HasIndex(e => new { e.UserId, e.MediaItemId }).IsUnique();

            // Índices compuestos de alta concurrencia para cargar listados optimizados
            entity.HasIndex(e => new { e.UserId, e.Status });
            entity.HasIndex(e => new { e.UserId, e.UserRating });

            entity.HasOne(e => e.User)
                .WithMany(u => u.LibraryItems)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.MediaItem)
                .WithMany()
                .HasForeignKey(e => e.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
