using Microsoft.EntityFrameworkCore;
using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.Dal
{
    public class VpnDbContext : DbContext
    {
        public DbSet<VlessProfile> VlessProfiles { get; set; }
        public DbSet<AccountInfo> AccountInfos { get; set; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string connectionDb = $"Filename={PathDB.GetPath("cat_client.db")}";
            optionsBuilder.UseSqlite(connectionDb);
        }
    }
}
