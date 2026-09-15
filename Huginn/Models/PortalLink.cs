using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Huginn.Models;

/// <summary>
/// Builds a link that opens the Azure portal's Logs view with a query already in it.
/// </summary>
/// <remarks>
/// The portal accepts a query in the URL when it is gzipped and base64 encoded, which is what its
/// own "copy link to query" produces. Without this a link can only name a blade, and finding one
/// operation among thousands is left to the reader.
/// </remarks>
public static class PortalLink
{
    /// <summary>Opens Logs for a resource with the query loaded and the time range matched to it.</summary>
    public static string ForLogs(string resourceId, string kql, int windowMinutes)
    {
        if (resourceId.Length == 0) return "";

        return "https://portal.azure.com/#blade/Microsoft_Azure_Monitoring_Logs/LogsBlade"
               + $"/resourceId/{Uri.EscapeDataString(resourceId)}"
               + "/source/LogsBlade.AnalyticsShareLinkToQuery"
               + $"/q/{Uri.EscapeDataString(Pack(kql))}"
               + $"/timespan/PT{Math.Max(5, windowMinutes)}M"
               + "/limit/1000/isQueryBase64Compressed/true";
    }

    private static string Pack(string kql)
    {
        using MemoryStream packed = new();

        // The portal reads gzip specifically, not raw deflate.
        using (GZipStream gzip = new(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(kql);
            gzip.Write(utf8, 0, utf8.Length);
        }

        return Convert.ToBase64String(packed.ToArray());
    }
}
