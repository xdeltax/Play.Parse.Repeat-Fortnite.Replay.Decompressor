using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private sealed record WatcherOptions(string ReplayDirectory, string OutputSubdirectory, int ScanIntervalSeconds, bool ProcessExisting);
    private sealed record PendingReplay(string Signature, DateTime FirstSeenUtc, DateTime NextProbeUtc, bool WaitingLineOpen);

    public static async Task Main(string[] args)
    {
        var options = ParseOptions(args);

        var outputDirectory = Path.Combine(options.ReplayDirectory, options.OutputSubdirectory);

        var processed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, PendingReplay>(StringComparer.OrdinalIgnoreCase);
        ReplayAnalyzer.ReplayProcessResult? lastProcessedResult = null;
        var initializedExisting = false;
        DateTime? lastMissingDirLog = null;
        var nextScanUtc = DateTime.UtcNow;

        Console.WriteLine($"Watching Fortnite replay directory: {options.ReplayDirectory}");
        Console.WriteLine($"Output directory: {outputDirectory}");
        Console.WriteLine($"Scan interval: {options.ScanIntervalSeconds} second(s)");
        Console.WriteLine("Press Ctrl+C or X to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        PrintShortcutHint(lastProcessedResult);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    HandleKeyboardInput(options.ReplayDirectory, outputDirectory, processed, pending, ref lastProcessedResult, cts);
                }

                if (DateTime.UtcNow >= nextScanUtc)
                {
                    if (!Directory.Exists(options.ReplayDirectory))
                    {
                        if (!lastMissingDirLog.HasValue || (DateTime.UtcNow - lastMissingDirLog.Value).TotalSeconds >= 30)
                        {
                            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Waiting for replay directory: {options.ReplayDirectory}");
                            lastMissingDirLog = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(outputDirectory);

                        if (!initializedExisting && !options.ProcessExisting)
                        {
                            var existingReplayFiles = Directory.EnumerateFiles(options.ReplayDirectory, "*.replay").ToList();
                            foreach (var replayFile in existingReplayFiles)
                            {
                                var signature = TryGetSignature(replayFile);
                                if (signature is not null)
                                {
                                    processed[replayFile] = signature;
                                }
                            }

                            initializedExisting = true;
                            Console.WriteLine($"Initialized with {processed.Count} existing replay file(s). New files will be processed.");
                        }

                        ScanAndProcess(options.ReplayDirectory, outputDirectory, processed, pending, ref lastProcessedResult);
                    }

                    nextScanUtc = DateTime.UtcNow.AddSeconds(options.ScanIntervalSeconds);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Scan failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        EnsurePendingProgressLineTerminated(pending);
        Console.WriteLine("Watcher stopped.");
    }

    private static void ScanAndProcess(
        string replayDirectory,
        string outputDirectory,
        IDictionary<string, string> processed,
        IDictionary<string, PendingReplay> pending,
        ref ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        var nowUtc = DateTime.UtcNow;
        var replayFiles = Directory.EnumerateFiles(replayDirectory, "*.replay").ToList();

        var existingSet = new HashSet<string>(replayFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var stalePending in pending.Keys.Where(key => !existingSet.Contains(key)).ToList())
        {
            if (pending.TryGetValue(stalePending, out var stale) && stale.WaitingLineOpen)
            {
                Console.WriteLine();
            }

            pending.Remove(stalePending);
        }

        foreach (var replayFile in replayFiles.OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            var signature = TryGetSignature(replayFile);
            if (signature is null)
            {
                continue;
            }

            if (processed.TryGetValue(replayFile, out var processedSignature) && processedSignature == signature)
            {
                pending.Remove(replayFile);
                continue;
            }

            if (!pending.TryGetValue(replayFile, out var pendingReplay))
            {
                if (CanReadReplayFile(replayFile))
                {
                    ProcessReplay(replayFile, signature, outputDirectory, processed, pending, showDetails: false, ref lastProcessedResult);
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Found replay but not readable yet: {replayFile}");
                Console.Write("Waiting for readability (check every 10s, timeout 30m): ");

                pending[replayFile] = new PendingReplay(signature, nowUtc, nowUtc.AddSeconds(10), true);
                continue;
            }

            if (pendingReplay.Signature != signature)
            {
                pendingReplay = pendingReplay with { Signature = signature };
                pending[replayFile] = pendingReplay;
            }

            if (nowUtc - pendingReplay.FirstSeenUtc >= TimeSpan.FromMinutes(30))
            {
                if (pendingReplay.WaitingLineOpen)
                {
                    Console.WriteLine();
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Skipping replay after 30 minutes unreadable: {replayFile}");
                processed[replayFile] = signature;
                pending.Remove(replayFile);
                continue;
            }

            if (nowUtc < pendingReplay.NextProbeUtc)
            {
                continue;
            }

            if (!CanReadReplayFile(replayFile))
            {
                Console.Write(".");
                pending[replayFile] = pendingReplay with { NextProbeUtc = nowUtc.AddSeconds(10), WaitingLineOpen = true };
                continue;
            }

            if (pendingReplay.WaitingLineOpen)
            {
                Console.WriteLine();
            }

            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Replay is now readable and will be processed: {replayFile}");
            ProcessReplay(replayFile, signature, outputDirectory, processed, pending, showDetails: false, ref lastProcessedResult);
        }
    }

    private static void HandleKeyboardInput(
        string replayDirectory,
        string outputDirectory,
        IDictionary<string, string> processed,
        IDictionary<string, PendingReplay> pending,
        ref ReplayAnalyzer.ReplayProcessResult? lastProcessedResult,
        CancellationTokenSource cts)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;

            if (key == ConsoleKey.X)
            {
                EnsurePendingProgressLineTerminated(pending);
                Console.WriteLine();
                Console.WriteLine("Exit requested by user (X).");
                cts.Cancel();
                return;
            }

            if (key == ConsoleKey.L)
            {
                EnsurePendingProgressLineTerminated(pending);
                ReparseLatestReplay(replayDirectory, outputDirectory, processed, pending, ref lastProcessedResult);
                continue;
            }

            if (key == ConsoleKey.R)
            {
                EnsurePendingProgressLineTerminated(pending);
                ReparseAllReplays(replayDirectory, outputDirectory, processed, pending, ref lastProcessedResult);
                continue;
            }

            if (key == ConsoleKey.D)
            {
                EnsurePendingProgressLineTerminated(pending);
                ShowLastReplayDetails(lastProcessedResult);
            }
        }
    }

    private static void ReparseLatestReplay(
        string replayDirectory,
        string outputDirectory,
        IDictionary<string, string> processed,
        IDictionary<string, PendingReplay> pending,
        ref ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        if (!Directory.Exists(replayDirectory))
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Cannot reparse latest replay, directory missing: {replayDirectory}");
            return;
        }

        var latestReplay = Directory
            .EnumerateFiles(replayDirectory, "*.replay")
            .Select(file => new { File = file, LastWriteUtc = File.GetLastWriteTimeUtc(file) })
            .OrderByDescending(item => item.LastWriteUtc)
            .Select(item => item.File)
            .FirstOrDefault();

        if (latestReplay is null)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] No replay file found for L command.");
            return;
        }

        var signature = TryGetSignature(latestReplay);
        if (signature is null || !CanReadReplayFile(latestReplay))
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Latest replay is currently not readable: {latestReplay}");
            return;
        }

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] L pressed: reparsing latest replay.");
        ProcessReplay(latestReplay, signature, outputDirectory, processed, pending, showDetails: false, ref lastProcessedResult);
    }

    private static void ReparseAllReplays(
        string replayDirectory,
        string outputDirectory,
        IDictionary<string, string> processed,
        IDictionary<string, PendingReplay> pending,
        ref ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        if (!Directory.Exists(replayDirectory))
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Cannot reparse all replays, directory missing: {replayDirectory}");
            return;
        }

        var replayFiles = Directory
            .EnumerateFiles(replayDirectory, "*.replay")
            .Select(file => new { File = file, LastWriteUtc = File.GetLastWriteTimeUtc(file) })
            .OrderBy(item => item.LastWriteUtc)
            .Select(item => item.File)
            .ToList();

        if (replayFiles.Count == 0)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] No replay files found for R command.");
            return;
        }

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] R pressed: reparsing {replayFiles.Count} replay file(s) oldest -> newest.");

        foreach (var replayFile in replayFiles)
        {
            var signature = TryGetSignature(replayFile);
            if (signature is null || !CanReadReplayFile(replayFile))
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Skipped unreadable replay during R: {replayFile}");
                continue;
            }

            ProcessReplay(replayFile, signature, outputDirectory, processed, pending, showDetails: false, ref lastProcessedResult);
        }
    }

    private static void ShowLastReplayDetails(ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        if (lastProcessedResult is null)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] D pressed: no replay has been processed yet.");
            PrintShortcutHint(lastProcessedResult);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] D pressed: showing details (including ranking) for {lastProcessedResult.ReplayFilePath}");
        Console.WriteLine(lastProcessedResult.AnalysisText);
        Console.WriteLine();
        PrintShortcutHint(lastProcessedResult);
    }

    private static bool CanReadReplayFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ProcessReplay(
        string replayFile,
        string signature,
        string outputDirectory,
        IDictionary<string, string> processed,
        IDictionary<string, PendingReplay> pending,
        bool showDetails,
        ref ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Processing replay: {replayFile}");

        try
        {
            var result = ReplayAnalyzer.ProcessReplayFile(replayFile, outputDirectory);
            lastProcessedResult = result;

            Console.WriteLine($"Saved JSON: {result.JsonPath}");
            Console.WriteLine($"Saved TXT:  {result.TxtPath}");
            Console.WriteLine();
            var consoleText = showDetails ? result.AnalysisText : GetSummaryAndOwnerOnly(result.AnalysisText);
            Console.WriteLine(consoleText);
            Console.WriteLine();
            PrintShortcutHint(lastProcessedResult);

            processed[replayFile] = signature;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to process replay '{replayFile}': {ex}");
        }
        finally
        {
            pending.Remove(replayFile);
        }
    }

    private static string GetSummaryAndOwnerOnly(string fullText)
    {
        var rankingHeader = $"{Environment.NewLine}Ranking";
        var index = fullText.IndexOf(rankingHeader, StringComparison.Ordinal);
        if (index < 0)
        {
            if (fullText.StartsWith("Ranking", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return fullText.TrimEnd();
        }

        return fullText[..index].TrimEnd();
    }

    private static void EnsurePendingProgressLineTerminated(IDictionary<string, PendingReplay> pending)
    {
        var hasOpenLine = false;
        foreach (var key in pending.Keys.ToList())
        {
            if (!pending.TryGetValue(key, out var value) || !value.WaitingLineOpen)
            {
                continue;
            }

            pending[key] = value with { WaitingLineOpen = false };
            hasOpenLine = true;
        }

        if (hasOpenLine)
        {
            Console.WriteLine();
        }
    }

    private static void PrintShortcutHint(ReplayAnalyzer.ReplayProcessResult? lastProcessedResult)
    {
        var lastReplayName = lastProcessedResult is null ? "none" : Path.GetFileName(lastProcessedResult.ReplayFilePath);
        Console.WriteLine($"[L] Last  [R] Reparse all  [D] Details last  [X] Exit | Last: {lastReplayName}");
    }

    private static string? TryGetSignature(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }
    }

    private static WatcherOptions ParseOptions(string[] args)
    {
        var replayDirectory = GetDefaultReplayDirectory();
        var outputSubdirectory = "PARSED";
        var scanIntervalSeconds = 2;
        var processExisting = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];

            if (string.Equals(current, "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                replayDirectory = Path.GetFullPath(args[++i]);
                continue;
            }

            if (string.Equals(current, "--output-subdir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var candidate = args[++i]?.Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    outputSubdirectory = candidate;
                }

                continue;
            }

            if (string.Equals(current, "--scan-interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var parsed) && parsed > 0)
                {
                    scanIntervalSeconds = parsed;
                }

                continue;
            }

            if (string.Equals(current, "--process-existing", StringComparison.OrdinalIgnoreCase))
            {
                processExisting = true;
            }
        }

        return new WatcherOptions(replayDirectory, outputSubdirectory, scanIntervalSeconds, processExisting);
    }

    private static string GetDefaultReplayDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "FortniteGame", "Saved", "Demos");
    }
}
