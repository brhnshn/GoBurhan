using Microsoft.EntityFrameworkCore;
using GoBurhan.Models;

namespace GoBurhan.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<ShortLink> ShortLinks => Set<ShortLink>();
        public DbSet<ClickAnalytics> ClickAnalytics => Set<ClickAnalytics>();
        public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<AdminUser>(entity =>
            {
                entity.ToTable("AdminUsers");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.Property(e => e.Username).IsRequired().HasMaxLength(150);
                entity.Property(e => e.PasswordHash).IsRequired();
                entity.Property(e => e.PasswordSalt).IsRequired();
                entity.Property(e => e.AuthToken).IsRequired().HasMaxLength(250);
            });

            modelBuilder.Entity<ShortLink>(entity =>
            {
                entity.ToTable("ShortLinks");
                entity.HasKey(e => e.Id);
                
                // ShortCode unique index
                entity.HasIndex(e => e.ShortCode)
                      .IsUnique();

                entity.Property(e => e.ShortCode)
                      .IsRequired()
                      .HasMaxLength(100);

                entity.Property(e => e.OriginalUrl)
                      .IsRequired();
            });

            modelBuilder.Entity<ClickAnalytics>(entity =>
            {
                entity.ToTable("ClickAnalytics");
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.ShortLink)
                      .WithMany(s => s.ClickAnalytics)
                      .HasForeignKey(e => e.ShortLinkId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
