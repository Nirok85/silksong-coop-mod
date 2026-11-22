using System.Diagnostics;
using System.Text.Json;
using SilksongCoop.Tests.EndToEnd;
using SilksongCoop.Tests.Reliability;

if (args.Length is not (4 or 5) ||
    !int.TryParse(args[0], out int baseSeed) ||
    !int.TryParse(args[1], out int seconds) || seconds < 3 ||
    (args.Length == 5 && args[4] is not ("udp" or "two" or "group")))
{
    Console.Error.WriteLine("Usage: <seed> <total-seconds> <report.json> <candidate-id> [udp|two|group]");
    return 2;
}

string output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var started = DateTimeOffset.UtcNow;
var total = Stopwatch.StartNew();
var phases = new List<Phase>();
string? error = null;
int scenarioSeed = baseSeed;
string status = "RUNNING";

try
{
    foreach (string profile in args.Length == 5 ? new[] { args[4] } : new[] { "udp", "two", "group" })
    {
        var phase = new Phase(profile);
        phases.Add(phase);
        var clock = Stopwatch.StartNew();
        double nextReport = 0;
        do
        {
            scenarioSeed = unchecked(baseSeed + phase.Completed);
            bool impaired = (scenarioSeed & 1) != 0;
            long begin = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            if (profile == "udp")
            {
                if (impaired)
                {
                    var result = EndToEndUdpReliabilityTests.ExerciseBackpressure();
                    phase.PeakQueue = Math.Max(phase.PeakQueue, result.PeakUnacknowledged);
                    phase.Dropped += result.DroppedDatagrams;
                }
                else
                {
                    new EndToEndUdpReliabilityTests().Client_Reconnects_ToTheSameHost_AndExchangesData();
                }
            }
            else
            {
                int nodes = profile == "two" ? 2 : 3 + ((scenarioSeed / 2) & 1);
                var observation = BeamSessionScenarios.Exercise(scenarioSeed, nodes, impaired);
                phase.PeakQueue = Math.Max(phase.PeakQueue, observation.PeakQueued);
                phase.Dropped += observation.Dropped;
                phase.Refused += observation.Refused;
                phase.Retries += observation.Retries;
            }

            phase.Record(Stopwatch.GetElapsedTime(begin).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
            if (impaired) phase.Impaired++;
            phase.Completed++;
            phase.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            if (phase.ElapsedSeconds >= nextReport)
            {
                Console.WriteLine(FormattableString.Invariant($"{profile}: {phase.ElapsedSeconds:F1}s, {phase.Completed} scenarios, peak {phase.PeakQueue}, drops {phase.Dropped}"));
                Save();
                nextReport = phase.ElapsedSeconds + 60;
            }
        }
        while (args.Length == 4 && clock.Elapsed.TotalSeconds < seconds / 3.0);
    }

    if (args.Length == 4 && phases.Any(p => p.Impaired == 0 || p.Dropped == 0 ||
        (p.Profile != "udp" && (p.Refused == 0 || p.Retries != p.Dropped))))
        throw new InvalidOperationException("An impairment profile did not exercise the expected loss and retry paths.");

    status = args.Length == 4 && seconds >= 2700 ? "PASS" : "SMOKE_PASS";
    return 0;
}
catch (Exception ex)
{
    error = ex.ToString();
    status = "FAIL";
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    Save();
}

void Save()
{
    var report = new
    {
        Candidate = args[3], BaseSeed = baseSeed, RequestedSeconds = seconds,
        StartedUtc = started, UpdatedUtc = DateTimeOffset.UtcNow, ElapsedSeconds = total.Elapsed.TotalSeconds,
        Status = status, Phases = phases, FirstFailure = error,
        FailureSeed = error == null ? (int?)null : scenarioSeed,
        Replay = error == null ? null : FormattableString.Invariant(
            $"dotnet run --project tools/SilksongCoop.Soak -c Release -- {scenarioSeed} 3 replay.json {args[3]} {phases[^1].Profile}")
    };
    string next = output + ".tmp";
    File.WriteAllText(next, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(next, output, overwrite: true);
}

sealed class Phase(string profile)
{
    public string Profile { get; } = profile;
    public string QueueMetric => Profile == "udp" ? "unacknowledged" : "inbound-and-reorder-buffer";
    public int Completed { get; set; }
    public int Impaired { get; set; }
    public int PeakQueue { get; set; }
    public long Dropped { get; set; }
    public long Refused { get; set; }
    public long Retries { get; set; }
    public double ElapsedSeconds { get; set; }
    public long AllocatedOnDriverThreadBytes { get; private set; }
    public double P50Ms => Percentile(0.50);
    public double P95Ms => Percentile(0.95);
    public double P99Ms => Percentile(0.99);

    // 0.1 ms steps up to 10 seconds. Storage is bounded by the histogram size.
    private readonly int[] _histogram = new int[100_001];

    public void Record(double ms, long allocated)
    {
        _histogram[Math.Min(_histogram.Length - 1, (int)Math.Ceiling(ms * 10))]++;
        AllocatedOnDriverThreadBytes += allocated;
    }

    private double Percentile(double quantile)
    {
        if (Completed == 0) return 0;
        int remaining = (int)Math.Ceiling(Completed * quantile);
        for (int i = 0; i < _histogram.Length; i++)
        {
            remaining -= _histogram[i];
            if (remaining <= 0) return i / 10.0;
        }
        return (_histogram.Length - 1) / 10.0;
    }
}
