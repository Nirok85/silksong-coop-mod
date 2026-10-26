using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Networking;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// In-game GUI overlay (toggled via F10) providing hosting, joining via room code or IP,
    /// Steam friend invite triggers, and live session status monitoring.
    /// </summary>
    public class CoopMenuUI : MonoBehaviour
    {
        public bool IsVisible { get; set; } = false;

        public void SetStatusMessage(string message)
        {
            _statusMessage = message;
        }

        private Rect _windowRect = new Rect(40, 40, 420, 480);
        private string _inputAddressOrCode = "";
        private string _statusMessage = "Ready. Press F10 to toggle this menu.";
        private readonly int _windowId; // Fix: Use unique window ID per instance

        private CoopSessionState? _sessionState;
        private INetworkTransport? _currentTransport;
        private SteamP2PTransport? _steamTransport;
        private LiteNetLibTransport? _udpTransport;

        public CoopMenuUI()
        {
            // Fix: Generate unique window ID to avoid conflicts with other mods
            _windowId = 9842 + UnityEngine.Random.Range(0, 1000);
        }

        public void Initialize(CoopSessionState sessionState, SteamP2PTransport? steamTransport, LiteNetLibTransport? udpTransport)
        {
            _sessionState = sessionState;
            _steamTransport = steamTransport;
            _udpTransport = udpTransport;
            _currentTransport = udpTransport; // Default fallback
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                IsVisible = !IsVisible;
            }

            // Keep Steam lobby id in sync with the transport (lobby created asynchronously)
            if (_steamTransport != null && _sessionState != null)
            {
                ulong lobbyVal = _steamTransport.CurrentLobbyId.m_SteamID;
                if (lobbyVal != 0 && _sessionState.SteamLobbyId != lobbyVal)
                {
                    _sessionState.SteamLobbyId = lobbyVal;
                }
            }
        }

        private void OnGUI()
        {
            // Always display a small watermark / hint in upper left with live connection status
            string watermark;
            Color watermarkColor;
            if (_sessionState != null && _sessionState.IsConnected)
            {
                string peerName = _sessionState.RemotePeerId.HasValue
                    ? _sessionState.RemotePeerId.Value.DisplayName
                    : "Partner";
                watermark = $"[F10] Co-op: Connected to {peerName}";
                watermarkColor = new Color(0.4f, 1f, 0.5f, 0.9f);
            }
            else if (_sessionState != null && _sessionState.Status == SessionStatus.Hosting)
            {
                watermark = "[F10] Co-op: Hosting (waiting for partner...)";
                watermarkColor = new Color(1f, 0.9f, 0.4f, 0.9f);
            }
            else if (_sessionState != null && _sessionState.Status == SessionStatus.Connecting)
            {
                watermark = "[F10] Co-op: Connecting...";
                watermarkColor = new Color(0.4f, 0.8f, 1f, 0.9f);
            }
            else
            {
                watermark = "[F10] Silksong Co-op Menu";
                watermarkColor = new Color(1f, 1f, 1f, 0.75f);
            }
            GUI.color = watermarkColor;
            GUI.Label(new Rect(10, 10, 360, 25), watermark);

            // Fix: Only draw spectator overlay when actually connected and in relevant state
            if (_sessionState != null && _sessionState.IsConnected)
            {
                DrawSpectatorOverlay();
            }

            if (!IsVisible) return;

            // Fix: Use random window ID to avoid conflicts with other mods
            GUI.backgroundColor = new Color(0.08f, 0.08f, 0.12f, 0.95f);
            _windowRect = GUI.Window(_windowId, _windowRect, DrawWindowContents, "SILKSONG COOPERATIVE MOD");
        }

        private void DrawSpectatorOverlay()
        {
            var coordinator = SilksongCoopPlugin.Instance?.DeathCoordinator;
            if (coordinator == null) return;

            if (coordinator.CurrentState == DeathState.Spectating)
            {
                float bannerWidth = 420f;
                float bannerHeight = 75f;
                float x = (Screen.width - bannerWidth) * 0.5f;
                float y = 35f;

                GUI.backgroundColor = new Color(0.04f, 0.04f, 0.08f, 0.90f);
                GUI.Box(new Rect(x, y, bannerWidth, bannerHeight), "");

                var titleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                string partnerName = (_sessionState != null && _sessionState.RemotePeerId.HasValue)
                    ? _sessionState.RemotePeerId.Value.DisplayName
                    : "Partner";
                GUI.color = new Color(0.4f, 0.8f, 1f, 1f);
                GUI.Label(new Rect(x, y + 8, bannerWidth, 24), $"SPECTATING {partnerName.ToUpperInvariant()}", titleStyle);

                int secondsLeft = Mathf.CeilToInt(coordinator.RemainingSpectatorTime);
                var subStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14
                };
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y + 34, bannerWidth, 22), $"Reviving beside partner in <color=#FFDD44>{secondsLeft}s</color>", subStyle);

                // Progress bar
                float total = Math.Max(15f, coordinator.ReviveCooldownDuration);
                float progress = Mathf.Clamp01(1f - (coordinator.RemainingSpectatorTime / total));
                float barWidth = bannerWidth - 40f;
                GUI.color = new Color(0.2f, 0.2f, 0.3f, 0.8f);
                GUI.DrawTexture(new Rect(x + 20, y + 60, barWidth, 6), Texture2D.whiteTexture);
                GUI.color = new Color(0.3f, 0.85f, 0.4f, 1f);
                GUI.DrawTexture(new Rect(x + 20, y + 60, barWidth * progress, 6), Texture2D.whiteTexture);
            }
            else if (coordinator.CurrentState == DeathState.TeamWipe)
            {
                float bannerWidth = 420f;
                float bannerHeight = 60f;
                float x = (Screen.width - bannerWidth) * 0.5f;
                float y = 35f;

                GUI.backgroundColor = new Color(0.12f, 0.02f, 0.02f, 0.92f);
                GUI.Box(new Rect(x, y, bannerWidth, bannerHeight), "");

                var titleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                GUI.color = new Color(1f, 0.3f, 0.3f, 1f);
                GUI.Label(new Rect(x, y + 8, bannerWidth, 24), "TEAM WIPE", titleStyle);

                var subStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 13
                };
                GUI.color = new Color(0.9f, 0.9f, 0.9f, 0.9f);
                GUI.Label(new Rect(x, y + 32, bannerWidth, 20), "Both heroes have fallen — Reloading at last bench...", subStyle);
            }
        }

        private void DrawWindowContents(int windowId)
        {
            GUI.DragWindow(new Rect(0, 0, 420, 25));

            GUILayout.Space(10);
            GUILayout.Label($"<b>Status:</b> {GetStatusDisplay()}", GUILayout.ExpandWidth(true));
            GUILayout.Label($"<b>Session Role:</b> {_sessionState?.Role.ToString() ?? "None"}");

            if (_sessionState != null && _sessionState.IsConnected)
            {
                GUILayout.Label($"<b>Connected Peer:</b> {_sessionState.RemotePeerId?.ToString() ?? "Unknown"}");
                GUILayout.Label($"<b>Round-Trip Time:</b> {_sessionState.RoundTripTimeMs} ms");
                GUILayout.Label($"<b>Packets:</b> Sent: {_sessionState.SentPackets} | Recv: {_sessionState.ReceivedPackets}");
            }

            GUILayout.Space(10);
            GUILayout.Box("", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            GUILayout.Space(10);

            if (_sessionState == null || !_sessionState.IsConnected)
            {
                // HOST SECTION
                GUILayout.Label("<b>HOST A SESSION</b>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host Direct UDP (Room Code)", GUILayout.Height(30)))
                {
                    HostUdpSession();
                }
                if (GUILayout.Button("Host Steam P2P Lobby", GUILayout.Height(30)))
                {
                    HostSteamSession();
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(15);

                // JOIN SECTION
                GUILayout.Label("<b>JOIN A SESSION</b>");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Code / IP:", GUILayout.Width(70));
                _inputAddressOrCode = GUILayout.TextField(_inputAddressOrCode);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Join via Room Code / UDP", GUILayout.Height(30)))
                {
                    JoinUdpSession(_inputAddressOrCode);
                }
                if (GUILayout.Button("Join via Steam Lobby ID", GUILayout.Height(30)))
                {
                    JoinSteamSession(_inputAddressOrCode);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                // CONNECTED SESSION CONTROLS
                if (!string.IsNullOrEmpty(_sessionState.CurrentRoomCode) && _sessionState.CurrentRoomCode != "Steam-Lobby")
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<b>Room Code:</b> {_sessionState.CurrentRoomCode}");
                    if (GUILayout.Button("Copy", GUILayout.Width(60)))
                    {
                        GUIUtility.systemCopyBuffer = _sessionState.CurrentRoomCode;
                        _statusMessage = "Room code copied to clipboard!";
                    }
                    GUILayout.EndHorizontal();
                }

                if (_sessionState.SteamLobbyId.HasValue)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<b>Steam Lobby ID:</b> {_sessionState.SteamLobbyId.Value}");
                    if (GUILayout.Button("Copy", GUILayout.Width(60)))
                    {
                        GUIUtility.systemCopyBuffer = _sessionState.SteamLobbyId.Value.ToString();
                        _statusMessage = "Steam lobby ID copied to clipboard!";
                    }
                    GUILayout.EndHorizontal();
                }

                if (_steamTransport != null && _sessionState.SteamLobbyId.HasValue)
                {
                    if (GUILayout.Button("Invite Steam Friends...", GUILayout.Height(30)))
                    {
                        _steamTransport.OpenInviteDialog();
                    }
                    GUILayout.Label("<color=#AAAAAA><i>Tip: friends can also accept an invite from the Steam overlay.</i></color>");
                }

                GUILayout.Space(15);
                if (GUILayout.Button("Disconnect Session", GUILayout.Height(35)))
                {
                    DisconnectSession();
                }
            }

            GUILayout.Space(15);
            GUILayout.Label($"<color=#AAAAAA>{_statusMessage}</color>");
        }

        private string GetStatusDisplay()
        {
            if (_sessionState == null) return "Uninitialized";
            return _sessionState.Status switch
            {
                SessionStatus.Disconnected => "<color=#FF6666>Disconnected</color>",
                SessionStatus.Hosting => "<color=#FFFF66>Hosting (Waiting for partner...)</color>",
                SessionStatus.Connecting => "<color=#66CCFF>Connecting...</color>",
                SessionStatus.Connected => "<color=#66FF66>Connected & Active</color>",
                SessionStatus.Spectating => "<color=#FFAA00>Spectating Partner</color>",
                SessionStatus.TeamWipe => "<color=#FF0000>Team Wipe (Reloading...)</color>",
                _ => _sessionState.Status.ToString()
            };
        }

        private void HostUdpSession()
        {
            try
            {
                if (_udpTransport == null)
                    _udpTransport = new LiteNetLibTransport(1, "Host");

                _udpTransport.StartHost(7777);
                _currentTransport = _udpTransport;

                // Fix: Detect actual local IP address instead of hardcoding localhost
                string localIp = GetLocalIpAddress();
                string code = RoomCodeCodec.Encode(localIp, 7777);

                _sessionState?.SetHosting(_udpTransport.LocalPeerId, code);
                _statusMessage = $"Hosting UDP on port 7777. Share room code: {code}";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Failed to host UDP: {ex.Message}";
            }
        }

        private string GetLocalIpAddress()
        {
            // Prefer a private LAN IPv4 address (10.x, 172.16-31.x, 192.168.x) over
            // loopback or virtual-adapter addresses, so room codes work across machines.
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                string? fallback = null;
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;

                    string s = ip.ToString();
                    if (s == "127.0.0.1")
                    {
                        fallback ??= s;
                        continue;
                    }

                    if (IsPrivateLanIp(s))
                    {
                        return s;
                    }

                    fallback ??= s;
                }

                if (fallback != null)
                    return fallback;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to detect local IP: {ex.Message}");
            }
            return "127.0.0.1"; // Fallback
        }

        private static bool IsPrivateLanIp(string ip)
        {
            if (ip.StartsWith("10.")) return true;
            if (ip.StartsWith("192.168.")) return true;
            if (ip.StartsWith("172."))
            {
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int octet))
                {
                    return octet >= 16 && octet <= 31;
                }
            }
            return false;
        }

        private void HostSteamSession()
        {
            try
            {
                if (_steamTransport == null)
                    _steamTransport = new SteamP2PTransport();

                _steamTransport.StartHost();
                _currentTransport = _steamTransport;
                SilksongCoopPlugin.Instance?.SetActiveTransport(_steamTransport);

                _sessionState?.SetHosting(_steamTransport.LocalPeerId, "Steam-Lobby");
                _statusMessage = "Steam P2P lobby created. Use Invite Friends button.";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Failed to host Steam: {ex.Message}. Ensure Steam is running and you are logged in. Use 'Host Direct UDP' as a fallback.";
            }
        }

        private void JoinUdpSession(string addressOrCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(addressOrCode))
                {
                    _statusMessage = "Please enter a valid room code (SK-XXXX-XXXX) or IP:Port.";
                    return;
                }

                if (_udpTransport == null)
                    _udpTransport = new LiteNetLibTransport(2, "Client");

                _currentTransport = _udpTransport;
                _udpTransport.Connect(addressOrCode.Trim());

                _sessionState?.SetConnecting(_udpTransport.LocalPeerId, addressOrCode);
                _statusMessage = $"Connecting to {addressOrCode}...";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Join error: {ex.Message}";
            }
        }

        private void JoinSteamSession(string lobbyIdString)
        {
            try
            {
                if (_steamTransport == null)
                    _steamTransport = new SteamP2PTransport();

                _currentTransport = _steamTransport;
                _steamTransport.Connect(lobbyIdString.Trim());
                SilksongCoopPlugin.Instance?.SetActiveTransport(_steamTransport);

                if (ulong.TryParse(lobbyIdString.Trim(), out ulong parsedLobby))
                {
                    if (_sessionState != null) _sessionState.SteamLobbyId = parsedLobby;
                }

                _sessionState?.SetConnecting(_steamTransport.LocalPeerId, lobbyIdString);
                _statusMessage = $"Joining Steam Lobby {lobbyIdString}...";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Steam join error: {ex.Message}";
            }
        }

        private void DisconnectSession()
        {
            _currentTransport?.Disconnect();
            _sessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);
            _statusMessage = "Session disconnected.";
        }
    }
}
