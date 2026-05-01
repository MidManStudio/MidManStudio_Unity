// MobileNetworkStatusMonitor.cs
// Monitors LAN / WiFi / hotspot / mobile-data status on mobile devices.
// Reports status changes via OnNetworkStatusChanged event.
// No game-specific dependencies.
//
// STATUS STRINGS:
//   "WIFI_CONNECTED"  — standard WiFi (can host and join)
//   "HOTSPOT"         — device is running a hotspot (can host, cannot join)
//   "MOBILE_DATA"     — cellular only (cannot host or join LAN game)
//   "NO_NETWORK"      — no connectivity

using System;
using System.Collections;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Netcode.LocalMultiplayer
{
    public class MobileNetworkStatusMonitor : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float       _checkInterval    = 2f;
        [SerializeField] private MID_LogLevel _logLevel        = MID_LogLevel.Info;

        public Action<string> OnNetworkStatusChanged;

        private string    _lastStatus = "";
        private bool      _monitoring;
        private Coroutine _coroutine;

        #region Lifecycle

        private void OnEnable()  => StartMonitoring();
        private void OnDisable() => StopMonitoring();

        #endregion

        #region Control

        public void StartMonitoring()
        {
            if (_monitoring) return;
            _monitoring = true;
            _coroutine  = StartCoroutine(MonitorLoop());
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
#elif UNITY_ANDROID || UNITY_IOS
            return GetDetailedMobileStatus();
#else
            return Application.internetReachability == NetworkReachability.NotReachable
                ? "NO_NETWORK" : "WIFI_CONNECTED";
#endif
        }

        private string GetDetailedMobileStatus()
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
                return "NO_NETWORK";

            try
            {
                bool hasWiFi    = false;
                bool hasHotspot = false;

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();

                        if (ip.StartsWith("192.168.43.") || ip.StartsWith("192.168.49.") ||
                            ip.StartsWith("172.20.10."))
                            hasHotspot = true;
                        else if (!ip.StartsWith("127."))
                            hasWiFi = true;
                    }
                }

                if (hasHotspot) return "HOTSPOT";
                if (hasWiFi)    return "WIFI_CONNECTED";
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
                    NetworkReachability.ReachableViaLocalAreaNetwork     => "WIFI_CONNECTED",
                    NetworkReachability.ReachableViaCarrierDataNetwork   => "MOBILE_DATA",
                    _ => "NO_NETWORK"
                };
            }
        }

        public bool CanHost()   => GetCurrentStatus() is "WIFI_CONNECTED" or "HOTSPOT";
        public bool CanJoin()   => GetCurrentStatus() is "WIFI_CONNECTED";
        public bool HasNetwork  => Application.internetReachability != NetworkReachability.NotReachable;

        public string GetStatusMessage() => GetCurrentStatus() switch
        {
            "WIFI_CONNECTED" => "Connected to WiFi",
            "HOTSPOT"        => "Mobile Hotspot Active",
            "MOBILE_DATA"    => "Using Mobile Data (WiFi required for LAN play)",
            "NO_NETWORK"     => "No Network Connection",
            _                => "Network Status Unknown"
        };

        #endregion
    }
}
