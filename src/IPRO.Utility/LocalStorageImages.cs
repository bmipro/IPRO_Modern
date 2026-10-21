using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// 585 (the owner's bench re-check, finding 6, 2026-10-21): on a developer's machine and on the bench,
// uploaded pictures live on the local storage emulator (Azurite, http://127.0.0.1:10000/devstoreaccount1),
// not on Azure's storage, and the Content-Security-Policy of the portal and of SuperAdmin allows pictures
// only from https://*.blob.core.windows.net: a logo replaced in the builder showed as a broken picture in
// the look. In Development only, and only when the configured storage is on this machine, its address is
// one more place pictures may come from. Anywhere else this adds nothing, so production's policy stays
// exactly as it is.
public static class LocalStorageImages
{
    // " http://127.0.0.1:10000" (with its leading space, to follow the policy's other sources), or "".
    public static string Source(bool isDevelopment, IConfiguration configuration)
    {
        if (!isDevelopment) return string.Empty;
        var connection = configuration["Azure:StorageConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) return string.Empty;
        Uri endpoint;
        try
        {
            // Reads the connection string only; nothing is asked of the storage.
            endpoint = new BlobServiceClient(connection).Uri;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
        return endpoint.IsLoopback ? " " + endpoint.GetLeftPart(UriPartial.Authority) : string.Empty;
    }
}
