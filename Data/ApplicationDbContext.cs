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
            modelBuilder.Entity<EmployeeMaster>().HasKey(x => x.EmployeeId);
            modelBuilder.Entity<LayoutTransaction>().HasKey(x => x.TransactionId);
        }
        public DbSet<CCLayout> CCLayouts { get; set; }
        public DbSet<LayoutMaster> LayoutMasters { get; set; }
        public DbSet<EmployeeMaster> EmployeeMasters { get; set; }
        public DbSet<LayoutTransaction> LayoutTransactions { get; set; }
    }
}