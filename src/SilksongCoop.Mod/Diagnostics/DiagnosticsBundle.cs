using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using SilksongCoop.Core.Diagnostics;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod.Diagnostics
{
    /// <summary>
    /// Builds a single text file that a player can attach to a bug report: environment,
    /// mod/session state, network metrics, sanitized config and the tail of BepInEx's log.
    /// Room codes and credential-shaped strings are redacted before anything is written.
    /// </summary>
    public static class DiagnosticsBundle
    {
        private const int LogTailLines = 400;

        /// <summary>
        /// Writes the bundle and returns its absolute path.
        /// </summary>
        /// <param name="plugin">Live plugin instance.</param>
        /// <exception cref="IOException">The destination cannot be created or written.</exception>
        public static string Save(SilksongCoopPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            string root = string.IsNullOrEmpty(Paths.BepInExRootPath)
                ? Directory.GetCurrentDirectory()
                : Paths.BepInExRootPath;
            string directory = Path.Combine(root, "SilksongCoop-diagnostics");
            Directory.CreateDirectory(directory);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(directory, $"silksong-coop-{stamp}.txt");

            var text = new StringBuilder(32 * 1024);
            text.AppendLine("Silksong Co-op Mod diagnostics bundle");
            text.AppendLine("=====================================");
            text.AppendLine($"generated_utc: {DateTime.UtcNow:O}");
            text.AppendLine();

            AppendEnvironment(text, plugin);
            AppendSession(text, plugin);
            AppendConfig(text, plugin);
            AppendLogTail(text, root);
            AppendNote(text);

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            SilksongCoopPlugin.LogInfo($"[Diagnostics] Wrote diagnostics bundle: {path}");
            return path;
        }

        private static void AppendEnvironment(StringBuilder text, SilksongCoopPlugin plugin)
        {
            text.AppendLine("[environment]");
            text.AppendLine($"mod_version: {SilksongCoopPlugin.PluginVersion}");
            text.AppendLine($"protocol_version: {NetworkProtocol.Version}");
            text.AppendLine($"game_version: {Application.version}");
            text.AppendLine($"unity_version: {Application.unityVersion}");
            text.AppendLine($"platform: {Application.platform}");
            text.AppendLine($"os: {Environment.OSVersion}");
            text.AppendLine($"runtime: {Environment.Version}");
            text.AppendLine($"bepinex_root: {Paths.BepInExRootPath}");
            text.AppendLine($"plugin_path: {Paths.PluginPath}");
            text.AppendLine($"scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            text.AppendLine();
        }

        private static void AppendSession(StringBuilder text, SilksongCoopPlugin plugin)
        {
            var session = plugin.SessionState;
            text.AppendLine("[session]");
            if (session == null)
            {
                text.AppendLine("state: unavailable");
                text.AppendLine();
                return;
            }

            text.AppendLine($"status: {session.Status}");
            text.AppendLine($"role: {session.Role}");
            text.AppendLine($"room_code: {(string.IsNullOrEmpty(session.CurrentRoomCode) ? "unset" : "set (redacted)")}");
            text.AppendLine($"steam_lobby: {(session.SteamLobbyId.HasValue ? session.SteamLobbyId.Value.ToString() : "none")}");
            text.AppendLine($"remote_peer: {(session.RemotePeerId.HasValue ? session.RemotePeerId.Value.DisplayName : "none")}");
            text.AppendLine($"rtt_ms: {session.RoundTripTimeMs}");
            text.AppendLine($"sent_packets: {session.SentPackets}");
            text.AppendLine($"received_packets: {session.ReceivedPackets}");
            text.AppendLine($"sent_bytes: {session.SentBytes}");
            text.AppendLine($"received_bytes: {session.ReceivedBytes}");
            text.AppendLine($"tick: {session.CurrentTick}");
            text.AppendLine($"tick_hz: {plugin.CurrentTickHz:F1}");

            if (session.RemotePeerId.HasValue &&
                plugin.CloneSpawner != null &&
                plugin.CloneSpawner.TryGetInterpolationDelayMs(session.RemotePeerId.Value, out float interpDelayMs))
            {
                text.AppendLine($"interpolation_delay_ms: {interpDelayMs:F1}");
            }

            var transport = plugin.ActiveTransport;
            if (transport != null)
            {
                text.AppendLine($"transport: {transport.GetType().Name}");
                text.AppendLine($"transport_connected: {transport.IsConnected}");
                text.AppendLine($"transport_rtt_ms: {transport.RoundTripTimeMs:F2}");
                text.AppendLine($"transport_loss_percent: {transport.PacketLossPercent:F2}");
            }
            else
            {
                text.AppendLine("transport: none");
            }

            text.AppendLine($"diagnostics: {plugin.MenuUI?.Diagnostics?.ExportSnapshot() ?? "unavailable"}");
            text.AppendLine();
        }

        private static void AppendConfig(StringBuilder text, SilksongCoopPlugin plugin)
        {
            text.AppendLine("[config]");
            text.AppendLine($"ReviveCooldownSeconds: {plugin.ReviveCooldownConfig?.Value}");
            text.AppendLine($"ReviveHealthPercent: {plugin.ReviveHealthPercentConfig?.Value}");
            text.AppendLine($"ReviveInvulnerabilitySeconds: {plugin.ReviveInvulnerabilityConfig?.Value}");
            text.AppendLine($"SyncQuestFlags: {plugin.SyncQuestFlagsConfig?.Value}");
            text.AppendLine($"EnablePlayer2DashTrail: {plugin.EnablePlayer2DashTrailConfig?.Value}");
            text.AppendLine($"Player2HueShift: {plugin.Player2HueShiftConfig?.Value}");
            text.AppendLine($"DefaultUdpPort: {plugin.DefaultUdpPortConfig?.Value}");
            text.AppendLine($"TickRateHz: {plugin.TickRateHzConfig?.Value}");
            text.AppendLine($"AutoReconnect: {plugin.AutoReconnectConfig?.Value}");
            text.AppendLine($"MaxReconnectAttempts: {plugin.MaxReconnectAttemptsConfig?.Value}");
            text.AppendLine($"SaveConnectionHistory: {plugin.SaveConnectionHistoryConfig?.Value}");
            text.AppendLine($"Language: {plugin.LanguageConfig?.Value}");
            text.AppendLine($"OnboardingCompleted: {plugin.OnboardingCompletedConfig?.Value}");
            text.AppendLine();
        }

        private static void AppendLogTail(StringBuilder text, string root)
        {
            text.AppendLine("[log_tail]");
            string? logPath = FindLogPath(root);
            if (logPath == null)
            {
                text.AppendLine("LogOutput.log not found");
                text.AppendLine();
                return;
            }

            text.AppendLine($"source: {logPath}");
            try
            {
                var tail = new Queue<string>(LogTailLines);
                foreach (string line in File.ReadLines(logPath))
                {
                    if (tail.Count == LogTailLines) tail.Dequeue();
                    tail.Enqueue(line);
                }

                foreach (string line in tail)
                {
                    text.AppendLine(LogRedactor.Redact(line));
                }
            }
            catch (Exception ex)
            {
                text.AppendLine($"failed to read log: {ex.GetType().Name}: {ex.Message}");
            }

            text.AppendLine();
        }

        private static string? FindLogPath(string bepinexRoot)
        {
            string[] candidates =
            {
                Path.Combine(bepinexRoot, "LogOutput.log"),
                Path.Combine(Environment.CurrentDirectory, "LogOutput.log")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }

            return null;
        }

        private static void AppendNote(StringBuilder text)
        {
            text.AppendLine("[note]");
            text.AppendLine("Attach this file to the bug report. Room codes and credential-shaped");
            text.AppendLine("strings are redacted, but review the log tail before publishing.");
        }
    }
}
