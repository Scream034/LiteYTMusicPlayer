using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Quality Switching

    public Task SwitchQualityAsync(AudioFormat format, int bitrate)
    {
        if (CurrentTrack == null) return Task.CompletedTask;

        ResetSealedFailedTrack();
        int session = BeginNewSession();

        var track = CurrentTrack;
        track.TransientFormat = format;
        track.TransientBitrate = bitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredFormat = format;
            track.PreferredBitrate = bitrate;
        }

        EnqueueCommand(new SwitchQualityCommand(track, CurrentPosition, format, bitrate, session));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Обработчик команды смены качества. Выполняется строго последовательно в actor-цикле.
    /// </summary>
    private async Task HandleSwitchQualityAsync(SwitchQualityCommand cmd)
    {
        var ct = GetSessionToken();
        if (_session.IsStale(cmd.Session)) return;

        try
        {
            var elapsed = (DateTime.UtcNow - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs)
                await Task.Delay(QualitySwitchCooldownMs - (int)elapsed, ct).ConfigureAwait(false);

            _lastQualitySwitchTime = DateTime.UtcNow;

            Log.Info($"[AudioEngine] SwitchQuality start: track={cmd.Track.Id}, requestedFormat={cmd.Track.TransientFormat?.ToContainerName() ?? "-"}, requestedBitrate={cmd.Track.TransientBitrate}, resumePos={cmd.Position.TotalMilliseconds}ms");

            Volatile.Write(ref _nTokenActiveTrackId, cmd.Track.Id);
            ct.ThrowIfCancellationRequested();

            var descriptor = await Task.Run(async () =>
                await _youtube.RefreshStreamAsync(cmd.Track, false, ct).ConfigureAwait(false)
                ?? await _youtube.RefreshStreamAsync(cmd.Track, true, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            if (descriptor == null)
            {
                if (!_session.IsStale(cmd.Session))
                    RaiseError(new InvalidOperationException("No stream available"));
                return;
            }

            if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id)) return;

            var d = descriptor.Value;

            Log.Info($"[AudioEngine] SwitchQuality resolved -> {d}");

            var currentInfo = _player.StreamInfo;
            if (currentInfo.IsValid
                && string.Equals(currentInfo.TrackId, d.TrackId, StringComparison.Ordinal)
                && currentInfo.Format == d.Format
                && currentInfo.CodecType == d.Codec
                && currentInfo.Bitrate == d.BitrateKbps)
            {
                Log.Info($"[AudioEngine] SwitchQuality skipped: active pipeline already matches");
                return;
            }

            if (d.HasPerceptualLufs)
            {
                cmd.Track.SetIntegratedLufs(d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
                CommitIntegratedLufs(cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
            }

            var previousTask = Volatile.Read(ref _activePlayTask);
            if (previousTask is { IsCompleted: false })
            {
                try { await previousTask.ConfigureAwait(false); } catch { }
            }

            if (_session.IsStaleOrCancelled(cmd.Session, ct)) return;

            var playTask = _player.PlayAsync(
                d,
                ct,
                seekPosition: cmd.Position.TotalSeconds > 1 ? cmd.Position : null);

            Volatile.Write(ref _activePlayTask, playTask);
            await playTask.ConfigureAwait(false);

            if (d.HasPerceptualLufs)
            {
                AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(
                    cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
            }

            ApplyGainToPipeline();
        }
        catch (Exception ex)
        {
            if (!_session.IsStaleOrCancelled(cmd.Session, ct) && !CancellationHelper.IsCancellationLike(ex))
            {
                AbortCurrentTrackPlaybackAfterFatalError(cmd.Track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, cmd.Track.Id);
        }
    }

    #endregion
}