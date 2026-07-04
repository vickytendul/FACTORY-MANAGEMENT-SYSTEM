using Microsoft.EntityFrameworkCore;
using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Zone> Zones { get; set; }
        public DbSet<Line> Lines { get; set; }
        public DbSet<CC> CCs { get; set; }
        public DbSet<OperationMaster> OperationMasters { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<OperationMaster>()
                .HasKey(x => x.OperationId);
        }
        public DbSet<CCLayout> CCLayouts { get; set; }
    }
}