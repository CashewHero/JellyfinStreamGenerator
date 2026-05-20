using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.StreamGenerator;

public interface IAdvancedTranscodeManager : ITranscodeManager
{
    IReadOnlyList<TranscodingJob> GetActiveTranscodingJobs();
}

public class AdvancedTranscodeManager(ITranscodeManager inner) : IAdvancedTranscodeManager
{
    private static FieldInfo? _activeJobsField;

    public IReadOnlyList<TranscodingJob> GetActiveTranscodingJobs()
    {
        _activeJobsField ??= inner.GetType()
            .GetField("_activeTranscodingJobs", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Cannot find _activeTranscodingJobs field on {inner.GetType().FullName}");

        var jobs = (List<TranscodingJob>)_activeJobsField.GetValue(inner)!;
        lock (jobs)
        {
            return [.. jobs];
        }
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId)
        => inner.GetTranscodingJob(playSessionId);

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
        => inner.GetTranscodingJob(path, type);

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        => inner.PingTranscodingJob(playSessionId, isUserPaused);

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
        => inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
        => inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
        => inner.StartFfMpeg(state, outputPath, commandLineArguments, userId, transcodingJobType, cancellationTokenSource, workingDirectory);

    public void OnTranscodeEndRequest(TranscodingJob job)
        => inner.OnTranscodeEndRequest(job);

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
        => inner.OnTranscodeBeginRequest(path, type);

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
        => inner.LockAsync(outputPath, cancellationToken);
}
