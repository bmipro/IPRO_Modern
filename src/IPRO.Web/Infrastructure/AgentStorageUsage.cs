using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// One shared definition of "how much storage has this agent used", because FileUploadCapacity is a
// single pool spanning documents, website photos, portal files, article images and gallery photos.
//
// 571: the definition itself now lives in IPRO.DataAccess (AgentStorage), where the admin app can
// reach it too. This class keeps its name so every caller in this app reads as before; each member
// forwards, so the two can never disagree.
public static class AgentStorageUsage
{
    public const int DefaultLimitMb = AgentStorage.DefaultLimitMb;

    public static Task<long> TotalBytesAsync(IPRODbContext db, int agentId) => AgentStorage.TotalBytesAsync(db, agentId);

    public static long LimitBytes(int? limitValueMb) => AgentStorage.LimitBytes(limitValueMb);

    public static int DisplayLimitMb(int? limitValueMb) => AgentStorage.DisplayLimitMb(limitValueMb);

    public static Task<long> GalleryBytesAsync(IPRODbContext db, int agentId) => AgentStorage.GalleryBytesAsync(db, agentId);

    public static double ToMb(long bytes) => AgentStorage.ToMb(bytes);
}
