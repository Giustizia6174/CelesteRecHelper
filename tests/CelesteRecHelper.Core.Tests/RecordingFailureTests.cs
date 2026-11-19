using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;
using CelesteRecHelper.Core.Persistence;
using CelesteRecHelper.Core.Recording;

namespace CelesteRecHelper.Core.Tests;

public sealed class RecordingFailureTests
{
    [Theory]
    [InlineData(204, "unsupported")]
    [InlineData(205, "generic")]
    [InlineData(207, "notready")]
    [InlineData(300, "parameters")]
    [InlineData(401, "parameters")]
    [InlineData(500, "running")]
    [InlineData(501, "notrunning")]
    [InlineData(502, "paused")]
    [InlineData(503, "notpaused")]
    [InlineData(504, "disabled")]
    [InlineData(600, "missing")]
    [InlineData(603, "resources")]
    [InlineData(604, "state")]
    [InlineData(700, "create")]
    [InlineData(701, "action")]
    [InlineData(702, "processing")]
    [InlineData(9999, "unknown")]
    public void ObsRejectionPreservesCodeRequestAndCommentWithoutGuessingAnUnderlyingCause(int code, string key)
    {
        var error = new ObsRequestException("SaveReplayBuffer", code, "Original OBS comment");
        var failure = RecordingFailureClassifier.Classify(error, RecordingFailureStage.Save);
        Assert.Equal(RecordingFailureKind.ObsRejected, failure.Kind);
        Assert.Equal("rechelper_error_obs_" + key, failure.MessageKey);
        Assert.Equal(code, failure.ObsCode);
        Assert.Equal("SaveReplayBuffer", failure.ObsRequest);
        Assert.Equal("Original OBS comment", failure.ObsComment);
        Assert.Contains("Original OBS comment", failure.Detail);
    }

    [Fact]
    public void TransportAndFileErrorsHaveSeparateCauses()
    {
        var transport = RecordingFailureClassifier.Classify(new ObsTransportException("Connection lost"), RecordingFailureStage.Save);
        var disk = RecordingFailureClassifier.Classify(new IOException("File access failed"), RecordingFailureStage.Save);
        Assert.Equal(RecordingFailureKind.Disconnected, transport.Kind);
        Assert.Equal(RecordingFailureKind.Storage, disk.Kind);
        Assert.Equal("rechelper_error_io", disk.MessageKey);
        Assert.Null(transport.ObsCode);
        Assert.Null(disk.ObsCode);
    }

    [Fact]
    public void TimeoutRemainsUnconfirmedAndManifestFailuresHaveTheirOwnStage()
    {
        var timeout = RecordingFailureClassifier.Classify(new OperationCanceledException(), RecordingFailureStage.Save);
        Assert.Equal(RecordingFailureKind.Timeout, timeout.Kind);
        Assert.Equal("rechelper_error_timeout", timeout.MessageKey);
        var disk = RecordingFailureClassifier.Classify(new UnauthorizedAccessException("Access denied"), RecordingFailureStage.Manifest);
        Assert.Equal(RecordingFailureStage.Manifest, disk.Stage);
        Assert.Equal("rechelper_error_manifest", disk.MessageKey);
    }

    [Fact]
    public void AnObservedBufferStopDoesNotInventAnObsResponseCode()
    {
        var failure = RecordingFailureClassifier.Classify(new ObsReplayStoppedException("Replay stopped"), RecordingFailureStage.Buffer);
        Assert.Equal(RecordingFailureKind.OutputStopped, failure.Kind);
        Assert.Equal("rechelper_error_buffer_stopped", failure.MessageKey);
        Assert.Null(failure.ObsCode);
        Assert.Null(failure.ObsRequest);
    }

    [Fact]
    public void FailureDetailsRoundTripAlongsideTheExistingManifestSchema()
    {
        using var workspace = new TestWorkspace();
        var route = workspace.NewRoute();
        route.LastFailure = RecordingFailureClassifier.Classify(new ObsRequestException("SaveReplayBuffer", 604, "Resource blocked"),
            RecordingFailureStage.Save);
        workspace.Store.Save(route);
        var loaded = workspace.Store.Load(route.RouteId);
        Assert.Equal(RouteManifest.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(route.LastFailure, loaded.LastFailure);
    }
}
