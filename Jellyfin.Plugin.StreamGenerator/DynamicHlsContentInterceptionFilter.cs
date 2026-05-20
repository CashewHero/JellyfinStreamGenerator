using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.StreamGenerator;

public class DynamicHlsContentInterceptionFilter(IAdvancedTranscodeManager advancedTranscodeManager, ILogger<DynamicHlsContentInterceptionFilter> logger) : IAsyncActionFilter
{
    private const string SessionPrefix = "sg_";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        if (!TryParseStreamGeneratorSegmentRequest(context, request, out var segmentId, out var configKey))
        {
            await next();
            return;
        }

        request.Headers["User-Agent"] = "StreamGenerator/1.0";

        var prefix = $"{SessionPrefix}{configKey}_";

        var activeJobs = advancedTranscodeManager.GetActiveTranscodingJobs();
        var candidateJobs = activeJobs
            .Where(j => j.PlaySessionId != null
                        && j.PlaySessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && !j.HasExited
                        && j.Path != null)
            .ToList();

        string? playSessionId = null;
        string? deviceId = null;

        // First: check if the segment file already exists on disk for any candidate session
        foreach (var job in candidateJobs)
        {
            var segmentPath = GetSegmentPath(job.Path!, segmentId, context);
            if (segmentPath != null && File.Exists(segmentPath))
            {
                playSessionId = job.PlaySessionId!;
                deviceId = job.DeviceId ?? playSessionId;
                logger.LogInformation(
                    "Reusing session {PlaySessionId} — segment {SegmentId} already exists on disk",
                    playSessionId, segmentId);
                break;
            }
        }

        // Second: check if any active transcode is close enough to produce this segment soon
        if (playSessionId == null)
        {
            foreach (var job in candidateJobs)
            {
                var currentIndex = GetCurrentTranscodingIndex(job.Path!);
                if (currentIndex == null) continue;

                var segmentLength = GetSegmentLength(context);
                var maxGap = 24 / segmentLength;

                if (segmentId >= currentIndex.Value && segmentId - currentIndex.Value <= maxGap)
                {
                    playSessionId = job.PlaySessionId!;
                    deviceId = job.DeviceId ?? playSessionId;
                    logger.LogInformation(
                        "Reusing session {PlaySessionId} — segment {SegmentId} within transcode reach (current: {CurrentIndex}, gap: {Gap})",
                        playSessionId, segmentId, currentIndex.Value, segmentId - currentIndex.Value);
                    break;
                }
            }
        }

        // Third: new session with random suffix
        if (playSessionId == null)
        {
            playSessionId = $"{prefix}{Random.Shared.Next():x8}";
            deviceId = playSessionId;
            logger.LogDebug("Assigning new transcode session: {PlaySessionId} for segment {SegmentId}",
                playSessionId, segmentId);
        }

        context.ActionArguments["playSessionId"] = playSessionId;
        if (context.ActionArguments.ContainsKey("deviceId"))
            context.ActionArguments["deviceId"] = deviceId!;

        if (context.ActionArguments.TryGetValue("streamOptions", out var optObj)
            && optObj is Dictionary<string, string> so)
        {
            if (so.ContainsKey("playSessionId")) so["playSessionId"] = playSessionId;
            if (so.ContainsKey("deviceId")) so["deviceId"] = deviceId!;
        }

        var queryDict = QueryHelpers.ParseQuery(request.QueryString.Value);
        queryDict["playSessionId"] = new StringValues(playSessionId);
        queryDict["deviceId"] = new StringValues(deviceId);
        request.QueryString = QueryString.Create(queryDict);

        await next();
    }

    private static bool TryParseStreamGeneratorSegmentRequest(
        ActionExecutingContext context,
        HttpRequest request,
        out int segmentId,
        out string configKey)
    {
        segmentId = 0;
        configKey = string.Empty;

        if (!context.RouteData.Values.TryGetValue("controller", out var c) || c is not "DynamicHls")
            return false;

        if (!context.RouteData.Values.TryGetValue("action", out var a))
            return false;

        var action = a?.ToString();
        if (action is not ("GetHlsVideoSegment" or "GetHlsAudioSegment"))
            return false;

        if (!context.ActionArguments.TryGetValue("playSessionId", out var psObj))
            return false;

        var playSessionIdStr = psObj?.ToString();
        if (playSessionIdStr is not "stream_generator_random"
            && playSessionIdStr?.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            // Also check query string
            if (!request.Query.TryGetValue("playSessionId", out var qps))
                return false;
            var qpsStr = qps.ToString();
            if (qpsStr is not "stream_generator_random"
                && !qpsStr.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (playSessionIdStr is not "stream_generator_random")
            return false;

        if (context.ActionArguments.TryGetValue("segmentId", out var segObj)
            && int.TryParse(segObj?.ToString(), out var sid))
        {
            segmentId = sid;
        }

        configKey = ComputeConfigKey(context, request);
        return true;
    }

    private static string ComputeConfigKey(ActionExecutingContext context, HttpRequest request)
    {
        var itemId = context.RouteData.Values.TryGetValue("itemId", out var id) ? id?.ToString() : "";
        var q = request.Query;
        var input = string.Join("|",
            itemId,
            q["mediaSourceId"].ToString(),
            q["videoCodec"].ToString(),
            q["audioCodec"].ToString(),
            q["audioStreamIndex"].ToString(),
            q["subtitleStreamIndex"].ToString(),
            q["subtitleMethod"].ToString(),
            q["videoBitrate"].ToString(),
            q["copyTimestamps"].ToString());

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    private static string? GetSegmentPath(string playlistPath, int segmentId, ActionExecutingContext context)
    {
        var folder = Path.GetDirectoryName(playlistPath);
        if (folder == null) return null;

        var filename = Path.GetFileNameWithoutExtension(playlistPath);
        var container = GetSegmentContainer(context);

        return Path.Combine(folder, filename + segmentId.ToString(CultureInfo.InvariantCulture) + container);
    }

    private static string GetSegmentContainer(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("segmentContainer", out var containerObj)
            && containerObj?.ToString() is { Length: > 0 } container)
        {
            return "." + container;
        }

        return ".ts";
    }

    private static int GetSegmentLength(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("segmentLength", out var lenObj)
            && int.TryParse(lenObj?.ToString(), out var len) && len > 0)
        {
            return len;
        }

        return 6;
    }

    private static int? GetCurrentTranscodingIndex(string playlistPath)
    {
        var folder = Path.GetDirectoryName(playlistPath);
        if (folder == null || !Directory.Exists(folder)) return null;

        var filePrefix = Path.GetFileNameWithoutExtension(playlistPath);

        try
        {
            var lastFile = Directory.EnumerateFiles(folder)
                .Select(f => Path.GetFileName(f))
                .Where(f => f.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var indexStr = Path.GetFileNameWithoutExtension(f.AsSpan()).Slice(filePrefix.Length);
                    return int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : (int?)null;
                })
                .Where(i => i.HasValue)
                .Max();

            return lastFile;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
