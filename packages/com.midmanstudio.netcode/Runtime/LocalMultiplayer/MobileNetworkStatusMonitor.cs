using System;
using System.Collections;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Netcode.LocalMultiplayer
{
    /// <summary>
    /// Monitors LAN / WiFi / hotspot / mobile-data status on mobile devices.
    /// Reports status changes via OnNetworkStatusChanged event.
    ///
    /// STATUS STRINGS:
    ///   "WIFI_CONNECTED"  — standard WiFi (can host and join)
    ///   "HOTSPOT"         — device is running a hotspot (can host, cannot join)
    ///   "MOBILE_DATA"     — cellular only (cannot host or join LAN game)
    ///   "NO_NETWORK"      — no connectivity
    ///
    /// Android: uses ConnectivityManager + NetworkCapabilities (current API,
    /// replaces the deprecated NetworkInfo/getType()) for connection type, and
    /// WifiManager.isWifiApEnabled() for hotspot state. There's no public Android
    /// API for reading hotspot state — isWifiApEnabled() is hidden but is the
    /// standard approach used across the ecosystem. If any native call fails for
    /// any reason (OEM restrictions, hidden-API lockdown on newer Android), this
    /// falls back to the old NetworkInterface IP-heuristic.
    /// iOS has no hotspot-state API at all, so it always uses the IP heuristic.
    /// </summary>
    public class MobileNetworkStatusMonitor : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float _checkInterval = 2f;
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        public Action<string> OnNetworkStatusChanged;

        private string _lastStatus = "";
        private bool _monitoring;
        private Coroutine _coroutine;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _activity;
        private AndroidJavaObject _wifiManager;
        private AndroidJavaObject _connectivityManager;
        private static int _transportWifi     = -1;
        private static int _transportCellular = -1;
        private bool _androidReady;
#endif

        #region Lifecycle

        private void OnEnable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            InitAndroidRefs();
#endif
            StartMonitoring();
        }

        private void OnDisable() => StopMonitoring();

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _wifiManager?.Dispose();
            _connectivityManager?.Dispose();
            _activity?.Dispose();
#endif
        }

        #endregion

        #region Control

        public void StartMonitoring()
        {
            if (_monitoring) return;
            _monitoring = true;
            _coroutine = StartCoroutine(MonitorLoop());
        }

        public void StopMonitoring()
        {
            if (!_monitoring) return;
            _monitoring = false;
            if (_coroutine != null) { StopCoroutine(_coroutine); _coroutine = null; }
        }

        public void ForceCheck()
        {
            string status = GetCurrentStatus();
            if (status == _lastStatus) return;
            _lastStatus = status;
            OnNetworkStatusChanged?.Invoke(status);
        }

        #endregion

        #region Status Detection

        private IEnumerator MonitorLoop()
        {
            while (_monitoring)
            {
                string status = GetCurrentStatus();
                if (status != _lastStatus)
                {
                    MID_Logger.LogDebug(_logLevel,
                        $"Network status: {_lastStatus} → {status}",
                        nameof(MobileNetworkStatusMonitor));
                    _lastStatus = status;
                    OnNetworkStatusChanged?.Invoke(status);
                }
                yield return new WaitForSeconds(_checkInterval);
            }
        }

        public string GetCurrentStatus()
        {
#if UNITY_EDITOR
            return "WIFI_CONNECTED";
#elif UNITY_ANDROID
            return GetAndroidStatus();
#elif UNITY_IOS
            return GetLegacyInterfaceStatus();
#else
            return Application.internetReachability == NetworkReachability.NotReachable
                ? "NO_NETWORK" : "WIFI_CONNECTED";
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR

        private void InitAndroidRefs()
        {
            if (_androidReady) return;
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity            = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                _wifiManager         = _activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                _connectivityManager = _activity.Call<AndroidJavaObject>("getSystemService", "connectivity");

                if (_transportWifi < 0)
                {
                    using var caps = new AndroidJavaClass("android.net.NetworkCapabilities");
                    _transportWifi     = caps.GetStatic<int>("TRANSPORT_WIFI");
                    _transportCellular = caps.GetStatic<int>("TRANSPORT_CELLULAR");
                }

                _androidReady = true;
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel,
                    $"Android network refs init failed, will use fallback checks: {e.Message}",
                    nameof(MobileNetworkStatusMonitor));
                _androidReady = false;
            }
        }

        private string GetAndroidStatus()
        {
            if (!_androidReady) InitAndroidRefs();
            if (!_androidReady) return GetLegacyInterfaceStatus();

            try
            {
                // Purpose-built check: is THIS device's own hotspot broadcasting
                // right now? No public Android API exists for this at all —
                // isWifiApEnabled() is hidden but is the long-standing community
                // standard for reading (not changing) AP state.
                if (_wifiManager.Call<bool>("isWifiApEnabled"))
                    return "HOTSPOT";
            }
            catch (Exception e)
            {
                MID_Logger.LogDebug(_logLevel,
                    $"isWifiApEnabled() unavailable on this device/OS ({e.GetType().Name}), " +
                    "continuing with ConnectivityManager checks.",
                    nameof(MobileNetworkStatusMonitor));
            }

            try
            {
                using var network = _connectivityManager.Call<AndroidJavaObject>("getActiveNetwork");
                if (network == null)
                {
                    return Application.internetReachability == NetworkReachability.ReachableViaCarrierDataNetwork
                        ? "MOBILE_DATA" : "NO_NETWORK";
                }

                using var caps = _connectivityManager.Call<AndroidJavaObject>("getNetworkCapabilities", network);
                if (caps == null) return "NO_NETWORK";

                if (caps.Call<bool>("hasTransport", _transportWifi))     return "WIFI_CONNECTED";
                if (caps.Call<bool>("hasTransport", _transportCellular)) return "MOBILE_DATA";

                return "NO_NETWORK";
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel,
                    $"ConnectivityManager check failed, falling back: {e.Message}",
                    nameof(MobileNetworkStatusMonitor));
                return GetLegacyInterfaceStatus();
            }
        }
#endif
        #endregion
        /// <summary>
        /// Last-resort fallback: iOS always uses this (no hotspot-state API exists
        /// there either), Android uses it only if the native calls above fail.
        /// Logs every detected interface/IP at Debug level so a wrong subnet
        /// prefix can be spotted and added.
        /// </summary>
        private string GetLegacyInterfaceStatus()
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
                return "NO_NETWORK";

            try
            {
                bool hasWiFi = false;
                bool hasHotspot = false;

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("127.")) continue;

                        MID_Logger.LogDebug(_logLevel,
                            $"Interface '{ni.Name}' ({ni.NetworkInterfaceType}) → {ip}",
                            nameof(MobileNetworkStatusMonitor));

                        if (ip.StartsWith("192.168.43.") || ip.StartsWith("192.168.49.") ||
                            ip.StartsWith("172.20.10.") || ip.StartsWith("192.168.137."))
                            hasHotspot = true;
                        else
                            hasWiFi = true;
                    }
                }

                if (hasHotspot) return "HOTSPOT";
                if (hasWiFi) return "WIFI_CONNECTED";
                if (Application.internetReachability == NetworkReachability.ReachableViaCarrierDataNetwork)
                    return "MOBILE_DATA";

                return "NO_NETWORK";
            }
            catch (Exception e)
            {
                MID_Logger.LogError(_logLevel, $"Status check error: {e.Message}",
                    nameof(MobileNetworkStatusMonitor));

                return Application.internetReachability switch
                {
                    NetworkReachability.ReachableViaLocalAreaNetwork => "WIFI_CONNECTED",
                    NetworkReachability.ReachableViaCarrierDataNetwork => "MOBILE_DATA",
                    _ => "NO_NETWORK"
                };
            }
        }

        public bool CanHost() => GetCurrentStatus() is "WIFI_CONNECTED" or "HOTSPOT";
        public bool CanJoin() => GetCurrentStatus() is "WIFI_CONNECTED";
        public bool HasNetwork => Application.internetReachability != NetworkReachability.NotReachable;

        public string GetStatusMessage() => GetCurrentStatus() switch
        {
            "WIFI_CONNECTED" => "Connected to WiFi",
            "HOTSPOT" => "Mobile Hotspot Active",
            "MOBILE_DATA" => "Using Mobile Data (WiFi required for LAN play)",
            "NO_NETWORK" => "No Network Connection",
            _ => "Network Status Unknown"
        };
    }
}