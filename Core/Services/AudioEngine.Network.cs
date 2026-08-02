using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Network Address Monitoring

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Log.Debug("[AudioEngine] NetworkAddressChanged event received");

        CancellationTokenSource newCts;
        lock (_networkRebuildLock)
        {
            _networkRebuildCts?.Cancel();
            _networkRebuildCts?.Dispose();
            _networkRebuildCts = newCts = CancellationTokenSource
                .CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        _ = RebuildNetworkClientsAfterDelayAsync(newCts.Token);
    }

    private async Task RebuildNetworkClientsAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            var currentIp = GetOutboundIp();
            if (currentIp == null)
            {
                Log.Debug("[AudioEngine] Network change ignored — no outbound route");
                return;
            }

            bool isTunAddress = IsVpnTunAddress(currentIp);

            if (!isTunAddress &&
                string.Equals(currentIp, _lastOutboundIp, StringComparison.Ordinal))
            {
                Log.Debug($"[AudioEngine] Network change ignored — outbound IP unchanged ({currentIp})");
                return;
            }

            if (isTunAddress)
            {
                Log.Info($"[AudioEngine] TUN/VPN address detected ({currentIp}) — diff bypass, rebuilding unconditionally.");
            }
            else
            {
                Log.Info($"[AudioEngine] Outbound IP changed: {_lastOutboundIp ?? "(none)"} → {currentIp}. Rebuilding.");
            }

            _lastOutboundIp = currentIp;
            await RebuildNetworkCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Network rebuild failed: {ex.Message}");
        }
    }

    #endregion

    #region Network Starvation Handling

    internal void NotifyNetworkStarvation()
    {
        Log.Info("[AudioEngine] Network starvation detected — forcing HTTP client rebuild");

        CancellationTokenSource newCts;
        lock (_networkRebuildLock)
        {
            _networkRebuildCts?.Cancel();
            _networkRebuildCts?.Dispose();
            _networkRebuildCts = newCts = CancellationTokenSource
                .CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        _ = ForceRebuildAfterStarvationAsync(newCts.Token);
    }

    private async Task ForceRebuildAfterStarvationAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            _lastOutboundIp = GetOutboundIp();

            Log.Info($"[AudioEngine] Force rebuild. Current outbound IP: {_lastOutboundIp ?? "(none)"}");
            await RebuildNetworkCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Force rebuild failed: {ex.Message}");
        }
    }

    #endregion

    #region Client Rebuild Core

    private async Task RebuildNetworkCoreAsync()
    {
        SharedHttpClient.Rebuild(_library.Settings.Proxy);
        _youtube.ReloadClient();

        Log.Info("[AudioEngine] HTTP clients rebuilt.");

        AudioSourceFactory.PreWarmCdnConnections(
            SharedHttpClient.Instance, _lifetimeCts.Token);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    #endregion

    #region Watchdog & Tunnel Helpers

    private async Task NetworkWatchdogAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _lifetimeCts.Token).ConfigureAwait(false);

            while (!_lifetimeCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(3), _lifetimeCts.Token).ConfigureAwait(false);

                var currentIp = GetOutboundIp();
                if (currentIp != null
                    && _lastOutboundIp != null
                    && !string.Equals(currentIp, _lastOutboundIp, StringComparison.Ordinal))
                {
                    Log.Info($"[AudioEngine] Watchdog: IP change missed by NetworkChange event: {_lastOutboundIp} → {currentIp}");
                    OnNetworkAddressChanged(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Network watchdog error: {ex.Message}");
        }
    }

    private static string? GetOutboundIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);

            socket.Connect("8.8.8.8", 65530);
            var ip = (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();

            Log.Debug($"[AudioEngine] GetOutboundIp: {ip ?? "(none)"}" +
                      (ip != null && IsVpnTunAddress(ip) ? " [TUN/VPN static — diff bypass]" : ""));

            return ip;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] GetOutboundIp failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsVpnTunAddress(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr))
            return false;

        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;

        if (bytes[0] == 198 && bytes[1] is 18 or 19) return true;
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return true;

        return false;
    }

    private void HandleCdnTunnelDead()
    {
        Log.Warn("[AudioEngine] CdnPreWarmer: tunnel dead detected — triggering proactive rebuild");
        NotifyNetworkStarvation();
    }

    #endregion
}