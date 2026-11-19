using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Media;

public static class OverlapMatcher
{
    /// <summary>Proves an overlap by one unambiguous run of identical, changing decoded frames.</summary>
    public static OverlapEvidence Match(IReadOnlyList<VideoFrameHash> replay, IReadOnlyList<VideoFrameHash> recording,
        double minimumSeconds = 0.5)
    {
        if (!double.IsFinite(minimumSeconds) || minimumSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(minimumSeconds));
        var matches = new List<(int Replay, int Record, int Count, int Distinct, double Seconds)>();
        // Each diagonal represents one alignment. Static and periodic footage can produce several equally plausible alignments.
        for (var offset = -recording.Count + 1; offset < replay.Count; offset++)
        {
            var i = Math.Max(0, offset);
            var j = Math.Max(0, -offset);
            var run = 0;
            var best = (Replay: 0, Record: 0, Count: 0, Distinct: 0, Seconds: 0.0);
            for (; i < replay.Count && j < recording.Count; i++, j++)
            {
                var same = replay[i].Hash == recording[j].Hash;
                var continuous = run == 0 || (replay[i].Seconds > replay[i - 1].Seconds &&
                    recording[j].Seconds > recording[j - 1].Seconds &&
                    Math.Abs((replay[i].Seconds - replay[i - 1].Seconds) -
                        (recording[j].Seconds - recording[j - 1].Seconds)) < 0.005);
                run = same ? (continuous ? run + 1 : 1) : 0;
                if (run < 2) continue;
                var seconds = Math.Min(replay[i].Seconds - replay[i - run + 1].Seconds,
                    recording[j].Seconds - recording[j - run + 1].Seconds);
                if (seconds < minimumSeconds || run <= best.Count) continue;
                var distinct = replay.Skip(i - run + 1).Take(run).Select(f => f.Hash).Distinct().Count();
                if (distinct >= 8) best = (i - run + 1, j - run + 1, run, distinct, seconds);
            }
            if (best.Count > 0) matches.Add(best);
        }
        if (matches.Count != 1)
            return new(OverlapStatus.Inconclusive, matches.Count == 0
                ? "No sufficiently long, changing frame sequence matched. Static footage, different encoding, or a gap needs review."
                : "More than one frame alignment matched. Repeated footage cannot prove a unique seam.", 0, 0, 0, null, null);
        var match = matches[0];
        var midpoint = match.Count / 2;
        return new(OverlapStatus.Verified, "Unique consecutive decoded frames overlap; a seam is available in both files.",
            match.Count, match.Distinct, match.Seconds, replay[match.Replay + midpoint].Seconds, recording[match.Record + midpoint].Seconds);
    }
}
