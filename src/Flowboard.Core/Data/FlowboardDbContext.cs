using Flowboard.Core.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Flowboard.Core.Data;

public sealed class FlowboardDbContext(DbContextOptions<FlowboardDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// All tables live in their own schema, so Flowboard can share a PostgreSQL database with other apps
    /// (the free demo shares one Neon database with another project).
    /// </summary>
    public const string Schema = "flowboard";

    /// <summary>Cookie encryption keys, kept in the database so sign-ins survive restarts and redeploys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<ApplicationUser>(e => e.Property(u => u.DisplayName).HasMaxLength(100));

        builder.Entity<Workspace>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(100);
            e.Property(w => w.InviteCode).HasMaxLength(32);
            e.HasIndex(w => w.InviteCode).IsUnique();
            e.HasMany(w => w.Members).WithOne().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(w => w.Boards).WithOne(b => b.Workspace).HasForeignKey(b => b.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Membership>(e =>
        {
            e.HasKey(m => new { m.WorkspaceId, m.UserId });
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId);
        });

        builder.Entity<Board>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(100);
            e.HasMany(b => b.Columns).WithOne().HasForeignKey(c => c.BoardId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Column>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(60);
            // Ordinal ("C") collation, so the database sorts ranks exactly like string.CompareOrdinal.
            e.Property(c => c.Rank).HasMaxLength(64).UseCollation("C");
            e.HasIndex(c => new { c.BoardId, c.Rank }).IsUnique();
            e.HasMany(c => c.Cards).WithOne().HasForeignKey(c => c.ColumnId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_columns_wip", "\"WipLimit\" IS NULL OR \"WipLimit\" > 0"));
        });

        builder.Entity<Card>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Description).HasMaxLength(4000);
            e.Property(c => c.Rank).HasMaxLength(64).UseCollation("C");
            e.Property(c => c.Version).IsConcurrencyToken();
            e.HasIndex(c => new { c.ColumnId, c.Rank }).IsUnique();
            e.HasIndex(c => c.BoardId);
            e.HasOne<Board>().WithMany().HasForeignKey(c => c.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Assignee).WithMany().HasForeignKey(c => c.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Activity>(e =>
        {
            e.Property(a => a.Text).HasMaxLength(500);
            e.HasIndex(a => new { a.BoardId, a.Id });
            e.HasOne<Board>().WithMany().HasForeignKey(a => a.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Actor).WithMany().HasForeignKey(a => a.ActorId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
