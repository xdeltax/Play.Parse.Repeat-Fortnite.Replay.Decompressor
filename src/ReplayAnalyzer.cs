using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortniteReplayReader;
using FortniteReplayReader.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unreal.Core.Models.Enums;

internal static class ReplayAnalyzer
{
    internal sealed record ReplayProcessResult(string ReplayFilePath, string JsonPath, string TxtPath, string JsonText, string AnalysisText);

    public static void Run(string[] args)
    {
        var options = ParseOptions(args);

        var serviceCollection = new ServiceCollection()
            .AddLogging(loggingBuilder => loggingBuilder
                .AddConsole()
                .SetMinimumLevel(LogLevel.Warning));
        var provider = serviceCollection.BuildServiceProvider();
        var loggerFactory = provider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("ReplayAnalyzer");

        IEnumerable<string> replayFiles = File.Exists(options.InputPath)
            ? new[] { options.InputPath }
            : Directory.EnumerateFiles(options.InputPath, "*.replay");

        Directory.CreateDirectory(options.OutputRoot);

        var sw = new Stopwatch();
        long total = 0;
        var hadFailure = false;

#if DEBUG
        var reader = new ReplayReader(logger, ParseMode.Normal);
#else
        var reader = new ReplayReader(null, ParseMode.Minimal);
#endif

        foreach (var replayFile in replayFiles)
        {
            sw.Restart();
            try
            {
                var replay = reader.ReadReplay(replayFile);
                WriteArtifacts(replay, replayFile, options.OutputRoot);

                if (!options.Quiet)
                {
                    PrintDiagnostics(replay);
                }
            }
            catch (Exception ex)
            {
                hadFailure = true;
                Console.WriteLine(ex);
            }

            sw.Stop();
            if (!options.Quiet)
            {
                Console.WriteLine($"---- {replayFile} : done in {sw.ElapsedMilliseconds} milliseconds ----");
            }

            total += sw.ElapsedMilliseconds;
        }

        if (!options.Quiet)
        {
            Console.WriteLine($"total: {total / 1000} seconds ----");
        }

        if (hadFailure)
        {
            Environment.ExitCode = 1;
        }
    }

    public static ReplayProcessResult ProcessReplayFile(string replayFilePath, string outputRoot)
    {
#if DEBUG
        var reader = new ReplayReader(null, ParseMode.Normal);
#else
        var reader = new ReplayReader(null, ParseMode.Minimal);
#endif

        var replay = reader.ReadReplay(replayFilePath);
        return WriteArtifacts(replay, replayFilePath, outputRoot);
    }

    private static ReplayProcessResult WriteArtifacts(FortniteReplay replay, string replayFilePath, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);

        var baseName = Path.GetFileNameWithoutExtension(replayFilePath);
        var jsonPath = Path.Combine(outputRoot, $"{baseName}.json");
        var txtPath = Path.Combine(outputRoot, $"{baseName}.txt");

        var jsonText = JsonSerializer.Serialize(replay, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        var analysisText = BuildTextAnalysis(replay, replayFilePath);

        File.WriteAllText(jsonPath, jsonText + Environment.NewLine);
        File.WriteAllText(txtPath, analysisText + Environment.NewLine);

        return new ReplayProcessResult(replayFilePath, jsonPath, txtPath, jsonText, analysisText);
    }

    private static string BuildTextAnalysis(FortniteReplay replay, string replayFilePath)
    {
        var players = replay.PlayerData?.ToList() ?? new List<PlayerData>();
        var playersById = players
            .Where(p => p.Id.HasValue)
            .GroupBy(p => p.Id!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var killFeed = replay.KillFeed?.ToList() ?? new List<KillFeedEntry>();

        var rankedPlayers = players
            .Where(p => p.Placement.HasValue && p.Placement.Value > 0)
            .OrderBy(p => p.Placement)
            .ThenByDescending(p => p.Kills ?? 0)
            .ThenBy(p => p.PlayerId ?? string.Empty)
            .ToList();
        var unrankedAliveRealPlayers = players
            .Where(p => !p.IsBot && (!p.Placement.HasValue || p.Placement.Value <= 0))
            .OrderByDescending(p => p.Kills ?? 0)
            .ThenBy(p => GetPlayerDisplayName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var owner = players.FirstOrDefault(p => p.IsReplayOwner);
        var ownerById = owner?.Id;
        var ownerFeedEvents = ownerById.HasValue
            ? killFeed.Where(k => k.FinisherOrDowner == ownerById.Value && !k.IsRevived).ToList()
            : new List<KillFeedEntry>();
        var ownerKnocks = ownerFeedEvents.Count(k => k.IsDowned);
        var ownerRankingKills = owner?.Kills ?? 0;
        var durationSeconds = replay.Info?.LengthInMs > 0 ? replay.Info.LengthInMs / 1000.0 : 0;
        var realPlayers = players.Count(p => !p.IsBot);
        var botPlayers = players.Count(p => p.IsBot && !string.IsNullOrWhiteSpace(p.BotId));
        var npcPlayers = players.Count(p => p.IsBot && string.IsNullOrWhiteSpace(p.BotId));
        var hardSum = realPlayers + botPlayers + npcPlayers;
        var totalPlayers = (uint)(realPlayers + botPlayers);
        static string FormatSummaryLine(string label, string value) => $"{label} {value}";

        var lines = new List<string>
        {
            $"File : {Path.GetFileNameWithoutExtension(replayFilePath)}",
            FormatSummaryLine("Match:", $"{ToIsoOrUnknown(replay.Info?.Timestamp)}; Duration: {FormatDurationMmSs(durationSeconds)}"),
            $"       {totalPlayers} Total Players",
            $"       {realPlayers} Real Players ",
            $"       {botPlayers} BOTs ",
            $"       {npcPlayers} NPCs",
            $"       Hard Count: Real {realPlayers}; BOT {botPlayers}; NPC {npcPlayers}; Sum {hardSum}"
        };

        if (owner is null)
        {
            lines.Add("Owner: unknown");
            lines.Add("       Accuracy unknown; Assists unknown; Revives unknown; Damage: unknown Given; unknown Received");
            lines.Add("       Rank#: unknown");
            lines.Add("       killed 0 / knocked 0; ");
            lines.Add("Owner Killfeed:");
            lines.Add("  (no kills)");
            lines.Add(string.Empty);
        }
        else
        {
            var ownerRank = owner.Placement.HasValue ? $"#{owner.Placement.Value:00}" : "unknown";
            var ownerDisplayId = GetPlayerDisplayName(owner);
            var ownerAssists = replay.Stats?.Assists;
            var ownerRevives = replay.Stats?.Revives;
            var ownerDamageGiven = replay.Stats?.DamageToPlayers;
            var ownerDamageReceived = replay.Stats?.DamageTaken;
            var ownerAccuracyPercent = replay.Stats is null ? (double?)null : replay.Stats.Accuracy * 100.0;

            var ownerEpicId = string.IsNullOrWhiteSpace(owner.PlayerId) ? "unknown" : owner.PlayerId;
            lines.Add($"Owner: {ownerDisplayId} ({ownerEpicId})");
            lines.Add($"       Rank#: {ownerRank}");
            lines.Add($"       killed {ownerRankingKills} / knocked {ownerKnocks}; ");

            if (owner.DeathTimeDouble.HasValue || owner.DeathTime.HasValue)
            {
                var deathTime = owner.DeathTimeDouble ?? owner.DeathTime;
                var ownerDeathEntry = ownerById.HasValue
                    ? killFeed
                        .Where(k => k.PlayerId == ownerById.Value && !k.IsRevived)
                        .OrderByDescending(k => GetEventTimeSeconds(k) ?? double.MinValue)
                        .FirstOrDefault()
                    : null;
                var killerType = "unknown";
                var killerIdOrName = ownerDeathEntry?.FinisherOrDownerName;
                if (ownerDeathEntry?.FinisherOrDowner is int killerId && playersById.TryGetValue(killerId, out var killerPlayer))
                {
                    killerType = GetDeathTypeLabel(killerPlayer);
                    killerIdOrName = GetPlayerDisplayName(killerPlayer);
                }
                else if (ownerDeathEntry is not null)
                {
                    killerType = ownerDeathEntry.FinisherOrDownerIsBot ? "BOT Player" : "Real Player";
                }

                if (string.IsNullOrWhiteSpace(killerIdOrName))
                {
                    killerIdOrName = "unknown";
                }

                lines.Add($"       Killed at {FormatDurationMmSs(deathTime.GetValueOrDefault())} by {killerType} ({killerIdOrName})");
            }

            lines.Add($"       Accuracy {(ownerAccuracyPercent.HasValue ? $"{ownerAccuracyPercent.Value:F1}%" : "unknown")}; Assists {FormatCompactMetric(ownerAssists)}; Revives {FormatCompactMetric(ownerRevives)}; Damage: {(ownerDamageGiven.HasValue ? ownerDamageGiven.Value.ToString() : "unknown")} Given; {(ownerDamageReceived.HasValue ? ownerDamageReceived.Value.ToString() : "unknown")} Received");

            lines.Add("Owner Killfeed:");

            if (ownerFeedEvents.Count == 0)
            {
                lines.Add("  (no kills)");
            }
            else
            {
                var killfeedVictimWidth = ownerFeedEvents
                    .Select(entry =>
                    {
                        PlayerData? victimPlayer = null;
                        if (entry.PlayerId.HasValue)
                        {
                            playersById.TryGetValue(entry.PlayerId.Value, out victimPlayer);
                        }

                        return victimPlayer is not null
                            ? GetPlayerDisplayName(victimPlayer)
                            : (!string.IsNullOrWhiteSpace(entry.PlayerName)
                                ? entry.PlayerName
                                : (entry.PlayerId.HasValue ? $"PlayerID:{entry.PlayerId.Value}" : "unknown"));
                    })
                    .DefaultIfEmpty("unknown")
                    .Max(name => name.Length);

                for (var i = 0; i < ownerFeedEvents.Count; i++)
                {
                    var entry = ownerFeedEvents[i];
                    var tag = entry.PlayerIsBot ? "BOT " : "Real";
                    var action = entry.IsDowned ? "knocked" : "killed!";
                    PlayerData? victimPlayer = null;
                    if (entry.PlayerId.HasValue)
                    {
                        playersById.TryGetValue(entry.PlayerId.Value, out victimPlayer);
                    }

                    if (victimPlayer is not null)
                    {
                        tag = GetKillfeedTag(victimPlayer);
                    }

                    var victim = victimPlayer is not null
                        ? GetPlayerDisplayName(victimPlayer)
                        : (!string.IsNullOrWhiteSpace(entry.PlayerName)
                            ? entry.PlayerName
                            : (entry.PlayerId.HasValue ? $"PlayerID:{entry.PlayerId.Value}" : "unknown"));
                    var time = GetEventTimeSeconds(entry);
                    var timeFmt = time.HasValue ? FormatDurationMmSs(time.Value) : "??:??";
                    var victimRank = victimPlayer?.Placement.HasValue == true ? victimPlayer.Placement.Value.ToString() : "??";
                    lines.Add($"  {i + 1,2}. [{timeFmt}] {action}: [{tag}] {victim.PadRight(killfeedVictimWidth)} | Rank: {victimRank}");
                }
            }

            lines.Add(string.Empty);
        }

        lines.Add("Ranking");

        if (rankedPlayers.Count == 0 && unrankedAliveRealPlayers.Count == 0)
        {
            lines.Add("No usable player data found.");
        }
        else
        {
            var allRankingPlayers = rankedPlayers.Concat(unrankedAliveRealPlayers).ToList();
            var idWidth = Math.Max(12, allRankingPlayers.Max(player => GetPlayerDisplayName(player).Length));

            foreach (var player in unrankedAliveRealPlayers)
            {
                var baseDisplayId = GetPlayerDisplayName(player);
                var kills = player.Kills ?? 0;
                var level = FormatLevel(player.Level);
                var seasonLevel = FormatLevel(player.SeasonLevelUIDisplay);
                var platform = string.IsNullOrWhiteSpace(player.Platform) ? "unknown" : player.Platform;
                var line = $"#?? * {baseDisplayId.PadRight(idWidth)} | Real  | Kills: {kills,2} | Level: {level} ({seasonLevel}) | Platform: {platform}";
                lines.Add(line);
            }

            foreach (var player in rankedPlayers)
            {
                var baseDisplayId = GetPlayerDisplayName(player);
                var kills = player.Kills ?? 0;
                string line;

                if (player.IsReplayOwner)
                {
                    var level = FormatLevel(player.Level);
                    var seasonLevel = FormatLevel(player.SeasonLevelUIDisplay);
                    var platform = string.IsNullOrWhiteSpace(player.Platform) ? "unknown" : player.Platform;
                    line = $"#{player.Placement.GetValueOrDefault(),2:00} * {baseDisplayId.PadRight(idWidth)} * OWNER * Kills: {kills,2} | Level: {level} ({seasonLevel}) | Platform: {platform}";
                }
                else if (!player.IsBot)
                {
                    var level = FormatLevel(player.Level);
                    var seasonLevel = FormatLevel(player.SeasonLevelUIDisplay);
                    var platform = string.IsNullOrWhiteSpace(player.Platform) ? "unknown" : player.Platform;
                    line = $"#{player.Placement.GetValueOrDefault(),2:00} * {baseDisplayId.PadRight(idWidth)} | Real  | Kills: {kills,2} | Level: {level} ({seasonLevel}) | Platform: {platform}";
                }
                else
                {
                    var type = string.IsNullOrWhiteSpace(player.BotId) ? "NPC  " : "BOT  ";
                    line = $"#{player.Placement.GetValueOrDefault(),2:00} | {baseDisplayId.PadRight(idWidth)} | {type} | Kills: {kills,2}";
                }

                lines.Add(line);
            }
        }

        lines.Add(string.Empty);

        return string.Join(Environment.NewLine, lines);
    }

    private static double? GetEventTimeSeconds(KillFeedEntry entry)
    {
        if (entry.ReplicatedWorldTimeSecondsDouble.HasValue)
        {
            return entry.ReplicatedWorldTimeSecondsDouble.Value;
        }

        if (entry.ReplicatedWorldTimeSeconds.HasValue)
        {
            return entry.ReplicatedWorldTimeSeconds.Value;
        }

        return null;
    }

    private static string GetPlayerDisplayName(PlayerData? player)
    {
        if (player is null)
        {
            return "unknown";
        }

        if (!string.IsNullOrWhiteSpace(player.PlayerName))
        {
            return player.PlayerName;
        }

        if (!string.IsNullOrWhiteSpace(player.PlayerNameCustomOverride))
        {
            return player.PlayerNameCustomOverride;
        }

        if (!string.IsNullOrWhiteSpace(player.PlayerId))
        {
            return player.PlayerId;
        }

        return "unknown";
    }

    private static string GetKillfeedTag(PlayerData? player)
    {
        if (player is null)
        {
            return "unknown";
        }

        if (!player.IsBot)
        {
            return "Real";
        }

        return string.IsNullOrWhiteSpace(player.BotId) ? "NPC " : "BOT ";
    }

    private static string GetDeathTypeLabel(PlayerData? player)
    {
        if (player is null)
        {
            return "unknown";
        }

        if (!player.IsBot)
        {
            return "Real Player";
        }

        return string.IsNullOrWhiteSpace(player.BotId) ? "NPC" : "BOT Player";
    }

    private static void PrintDiagnostics(FortniteReplay replay)
    {
        var players = replay.PlayerData?.ToList() ?? new List<PlayerData>();
        var replayOwners = players.Where(p => p.IsReplayOwner).ToList();

        Console.WriteLine("Replay Owner Diagnostics");
        Console.WriteLine("----------------------");
        Console.WriteLine($"RecorderId (GameData): {replay.GameData?.RecorderId?.ToString() ?? "null"}");
        Console.WriteLine($"PlayerData Count:       {players.Count}");
        Console.WriteLine($"ReplayOwner Count:      {replayOwners.Count}");

        if (replayOwners.Count == 0)
        {
            Console.WriteLine("ReplayOwner Players:    (none)");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("ReplayOwner Players:");
        foreach (var owner in replayOwners)
        {
            Console.WriteLine($"  - Id={owner.Id?.ToString() ?? "null"}, PlayerId={owner.PlayerId ?? "null"}, Name={owner.PlayerName ?? owner.PlayerNameCustomOverride ?? "null"}, IsBot={owner.IsBot}");
        }

        Console.WriteLine();
    }

    private static string NormalizeFriendlyName(string? friendlyName, string replayFilePath)
    {
        var name = (friendlyName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Path.GetFileNameWithoutExtension(replayFilePath);
        }

        if (string.Equals(name, "ungespeicherte wiederholung", StringComparison.OrdinalIgnoreCase))
        {
            return "Unsaved Replay";
        }

        if (string.Equals(name, "unbenannt", StringComparison.OrdinalIgnoreCase))
        {
            return "Untitled Replay";
        }

        return name;
    }

    private static string FormatCompactMetric(uint? value)
    {
        return value.HasValue ? value.Value.ToString() : "unknown";
    }

    private static string ToIsoOrUnknown(DateTime? dateTime)
    {
        if (!dateTime.HasValue)
        {
            return "unknown";
        }

        return dateTime.Value.ToUniversalTime().ToString("O");
    }

    private static string FormatDurationMmSs(double totalSeconds)
    {
        var safeSeconds = Math.Max(0, totalSeconds);
        var minutes = (int)Math.Floor(safeSeconds / 60);
        var seconds = (int)Math.Floor(safeSeconds % 60);
        return $"{minutes:00}:{seconds:00}";
    }

    private static string FormatLevel(int? value)
    {
        return value.HasValue ? value.Value.ToString().PadLeft(3) : "???";
    }

    private static string FormatLevel(uint? value)
    {
        return value.HasValue ? value.Value.ToString().PadLeft(3) : "???";
    }

    private static AppOptions ParseOptions(string[] args)
    {
        var inputPath = @"C:\Users\ferro\Downloads\";
        var workspaceRoot = ResolveWorkspaceRoot();
        var outputRoot = Path.Combine(workspaceRoot, "REPLAYS", "PARSED");
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (current == "--output-root" && i + 1 < args.Length)
            {
                outputRoot = Path.GetFullPath(args[++i]);
                continue;
            }

            if (current == "--quiet")
            {
                quiet = true;
                continue;
            }

            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                inputPath = Path.GetFullPath(current);
            }
        }

        return new AppOptions(inputPath, outputRoot, quiet);
    }

    private static string ResolveWorkspaceRoot()
    {
        var current = Directory.GetCurrentDirectory();

        while (true)
        {
            if (Directory.Exists(Path.Combine(current, "REPLAYS")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return Directory.GetCurrentDirectory();
            }

            current = parent.FullName;
        }
    }

    private sealed record AppOptions(string InputPath, string OutputRoot, bool Quiet);
}
