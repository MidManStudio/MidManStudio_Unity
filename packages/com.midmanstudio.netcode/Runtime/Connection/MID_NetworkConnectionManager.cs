// MID_NetworkConnectionManager.cs
// Generic internet connectivity monitor.
// Fires onConnectionStatusChanged when connectivity state changes.
// No game-specific dependencies.
//
// USAGE:
//   MID_NetworkConnectionManager.StartContinuousCheck();
//   MID_NetworkConnectionManager.onConnectionStatusChanged += OnConnected;
//   bool ok = await MID_NetworkConnectionManager.ConfirmConnectionAsync();

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Singleton;

namespace MidManStudio.Core.Netcode
{
    public enum ConnectionCheckMethod
    {
        Ping,
        HttpRequest,
        DnsLookup,
        TcpConnection,
        HttpPing,
    }

    public class MID_NetworkConnectionManager : Singleton<MID_NetworkConnectionManager>
    {
        #region Constants

        private const string PingHost       = "1.1.1.1";          // Cloudflare DNS
        private const string HttpCheckUrl   = "https://unity3d.com";
        private const string HttpPingUrl    = "https://speed.cloudflare.com";
        private const string DnsDomain      = "cloudflare.com";
        private const string TcpHost        = "cloudflare.com";
        private const int    TcpPort        = 443;
        private const int    DefaultInterval = 5000;               // ms

        #endregion

        #region Serialized Fields

        [SerializeField] private MID_LogLevel        _logLevel         = MID_LogLevel.Info;
        [SerializeField] private ConnectionCheckMethod _checkMethod     = ConnectionCheckMethod.Ping;
        [SerializeField] private int                  _checkIntervalMs = DefaultInterval;
        [SerializeField] private int                  _timeoutMs        = 5000;

        #endregion

        #region Events

        /// <summary>Fires on the main thread whenever connection state changes.</summary>
        public static event Action<bool> onConnectionStatusChanged;

        /// <summary>Fires whenever a check completes (connected or not).</summary>
        public static event Action<bool> onCheckCompleted;

        #endregion

        #region Public Properties

        public static bool IsConnected => Instance?._connected ?? false;
        public static bool IsChecking  => Instance?._checking  ?? false;

        #endregion

        #region Private State

        private bool                    _connected;
        private bool                    _checking;
        private bool                    _destroying;
        private int                     _currentIntervalMs;
        private CancellationTokenSource _cts;
        private readonly HttpClient     _httpClient = new HttpClient();

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            Remake(true);                    // persist across scenes
            _currentIntervalMs = _checkIntervalMs;
        }

        protected override void OnDestroy()
        {
            _destroying = true;
            CleanupResources();
            base.OnDestroy();
        }

        private void OnApplicationPause(bool paused) { /* hook for subclasses */ }
        private void OnApplicationFocus(bool focus)  { /* hook for subclasses */ }

        #endregion

        #region Public API

        /// <summary>Begin continuous background connectivity checks.</summary>
        public static void StartContinuousCheck()
        {
            Instance?.StartChecking();
        }

        /// <summary>Stop the background check loop.</summary>
        public static void StopContinuousCheck()
        {
            Instance?.StopChecking();
        }

        /// <summary>Perform a single connectivity check and return the result.</summary>
        public static async Task<bool> ConfirmConnectionAsync()
        {
            if (Instance == null) return false;
            return await Instance.SingleCheckAsync();
        }

        /// <summary>
        /// Multiply the normal check interval temporarily.
        /// Useful when showing an error popup — slow down polling to reduce noise.
        /// Pass 1.0 to restore the default interval.
        /// </summary>
        public static void SetIntervalMultiplier(float multiplier)
        {
            if (Instance == null) return;
            Instance._currentIntervalMs =
                Mathf.Max(1000, Mathf.RoundToInt(Instance._checkIntervalMs * multiplier));
        }

        /// <summary>Change the check method at runtime.</summary>
        public static void SetCheckMethod(ConnectionCheckMethod method)
        {
            if (Instance != null) Instance._checkMethod = method;
        }

        #endregion

        #region Private — Checking Loop

        private void StartChecking()
        {
            if (_checking)
            {
                MID_Logger.LogDebug(_logLevel, "Already checking — ignoring start request.",
                    nameof(MID_NetworkConnectionManager));
                return;
            }

            _checking = true;
            _cts      = new CancellationTokenSource();
            RunCheckLoop(_cts.Token);

            MID_Logger.LogInfo(_logLevel, "Continuous connectivity check STARTED.",
                nameof(MID_NetworkConnectionManager));
        }

        private void StopChecking()
        {
            if (!_checking) return;
            _checking = false;
            _cts?.Cancel();

            MID_Logger.LogInfo(_logLevel, "Continuous connectivity check STOPPED.",
                nameof(MID_NetworkConnectionManager));
        }

        private async void RunCheckLoop(CancellationToken token)
        {
            while (_checking && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_currentIntervalMs, token);
                    if (!token.IsCancellationRequested)
                        await PerformCheckAndNotify();
                }
                catch (TaskCanceledException) { break; }
                catch (Exception e)
                {
                    MID_Logger.LogError(_logLevel,
                        $"Check loop exception: {e.Message}",
                        nameof(MID_NetworkConnectionManager));
                }
            }
        }

        private async Task<bool> PerformCheckAndNotify()
        {
            bool result = await RunCheck();
            bool changed = result != _connected;
            _connected = result;

            onCheckCompleted?.Invoke(result);
            if (changed) onConnectionStatusChanged?.Invoke(result);

            MID_Logger.LogDebug(_logLevel,
                $"Check result: {result} (changed={changed})",
                nameof(MID_NetworkConnectionManager));

            return result;
        }

        private async Task<bool> SingleCheckAsync()
        {
            bool wasChecking = _checking;
            if (wasChecking) { StopChecking(); await Task.Delay(150); }

            bool result = await RunCheck();
            _connected  = result;
            onCheckCompleted?.Invoke(result);

            if (wasChecking) StartChecking();
            return result;
        }

        #endregion

        #region Private — Check Methods

        private Task<bool> RunCheck()
        {
            return _checkMethod switch
            {
                ConnectionCheckMethod.Ping          => CheckPing(),
                ConnectionCheckMethod.HttpRequest    => CheckHttpRequest(),
                ConnectionCheckMethod.DnsLookup      => CheckDns(),
                ConnectionCheckMethod.TcpConnection  => CheckTcp(),
                ConnectionCheckMethod.HttpPing        => CheckHttpPing(),
                _                                    => CheckPing()
            };
        }

        private async Task<bool> CheckPing()
        {
            try
            {
                var ping = new Ping(PingHost);
                float elapsed = 0f;
                while (!ping.isDone && elapsed < _timeoutMs / 1000f)
                {
                    await Task.Yield();
                    elapsed += Time.unscaledDeltaTime;
                }
                return ping.isDone && ping.time >= 0;
            }
            catch { return false; }
        }

        private async Task<bool> CheckHttpRequest()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var request = UnityWebRequest.Get(HttpCheckUrl);
                request.timeout = _timeoutMs / 1000;
                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) return true;
                if (attempt < 1) await Task.Delay(1000);
            }
            return false;
        }

        private async Task<bool> CheckDns()
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(DnsDomain);
                return entry.AddressList.Length > 0;
            }
            catch { return false; }
        }

        private async Task<bool> CheckTcp()
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(TcpHost, TcpPort);
                if (await Task.WhenAny(task, Task.Delay(_timeoutMs)) == task)
                    return client.Connected;
                return false;
            }
            catch { return false; }
        }

        private async Task<bool> CheckHttpPing()
        {
            try
            {
                _httpClient.Timeout = TimeSpan.FromMilliseconds(_timeoutMs);
                var response = await _httpClient.GetAsync(HttpPingUrl);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // Synchronous fallback for critical operations (blocks caller briefly)
        public static bool CheckSynchronous()
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(PingHost, 53, null, null);
                bool ok = result.AsyncWaitHandle.WaitOne(3000);
                if (ok) client.EndConnect(result);
                return ok;
            }
            catch { return false; }
        }

        #endregion

        #region Private — Cleanup

        private void CleanupResources()
        {
            _checking = false;
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            try { _httpClient?.Dispose(); } catch { }
        }

        #endregion
    }
}
