using NVPN.Cross.Dal.Models;

namespace NVPN.Cross.BL.Services.Interfaces
{
    public interface IVlessConfigService
    {
        Task SaveConfigAsync(string config);
        Task<List<VlessProfile>> GetConfigsAsync();
        Task DeleteConfigAsync(VlessProfile config);
    }
}
