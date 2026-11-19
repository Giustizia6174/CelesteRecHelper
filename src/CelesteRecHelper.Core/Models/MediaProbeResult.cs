using System.Text.Json;

namespace CelesteRecHelper.Core.Models;

public sealed record AudioTrackInfo(int Index, string Codec, int SampleRate, int Channels,
    string? ChannelLayout, string? Language);
public sealed record MediaProbeResult(string FilePath, long FileSizeBytes, double DurationSeconds,
    double FirstVideoFrameSeconds, double LastVideoFrameSeconds, int Width, int Height,
    string VideoCodec, string FrameRate, IReadOnlyList<AudioTrackInfo> AudioTracks, JsonElement RawMetadata);
