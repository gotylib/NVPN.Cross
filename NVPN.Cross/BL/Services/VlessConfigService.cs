using Microsoft.EntityFrameworkCore;
using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal;
using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.BL.Services
{
    public class VlessConfigService : IVlessConfigService
    {
        private readonly VpnDbContext _db;

        public VlessConfigService(VpnDbContext db) => _db = db;

        public async Task<List<VlessProfile>> GetConfigsAsync()
        {
            return await _db.VlessProfiles.ToListAsync();
        }

        public async Task DeleteConfigAsync(VlessProfile config)
        {
            _db.VlessProfiles.Remove(config);
            await _db.SaveChangesAsync();
        }

        public async Task SaveConfigAsync(string config)
        {
            _db.VlessProfiles.Add(VlessProfile.ParseVlessUrl(config));
            await _db.SaveChangesAsync();
        }
        
    }
}
