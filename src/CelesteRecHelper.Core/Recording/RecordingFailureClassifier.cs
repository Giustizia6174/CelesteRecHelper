using System.Net.WebSockets;
using CelesteRecHelper.Core.Models;
using CelesteRecHelper.Core.Obs;

namespace CelesteRecHelper.Core.Recording;

public static class RecordingFailureClassifier
{
    public static RecordingFailure Classify(Exception error, RecordingFailureStage stage)
    {
        if (stage == RecordingFailureStage.Manifest)
            return new(RecordingFailureKind.Storage, stage, "rechelper_error_manifest", error.Message);
        if (error is ObsRequestException obs)
        {
            // obs-websocket docs/generated/protocol.md RequestStatus (RPC 1).
            // Unknown codes retain their code/comment instead of inferring disk or encoder failures.
            var key = obs.Code switch
            {
                204 => "unsupported", 205 => "generic", 207 => "notready",
                300 or 301 or >= 400 and <= 404 => "parameters",
                500 => "running", 501 => "notrunning", 502 => "paused", 503 => "notpaused", 504 => "disabled",
                600 => "missing", 603 => "resources", 604 => "state",
                700 => "create", 701 => "action", 702 => "processing", _ => "unknown"
            };
            return new(RecordingFailureKind.ObsRejected, stage, "rechelper_error_obs_" + key,
                obs.Message, obs.Code, obs.RequestType, obs.Comment);
        }
        return error switch
        {
            ObsReplayStoppedException => new(RecordingFailureKind.OutputStopped, stage, "rechelper_error_buffer_stopped", error.Message),
            ObsAuthenticationException => new(RecordingFailureKind.Authentication, stage, "rechelper_error_auth", error.Message),
            OperationCanceledException or TimeoutException => new(RecordingFailureKind.Timeout, stage, "rechelper_error_timeout", error.Message),
            ObsTransportException or WebSocketException => new(RecordingFailureKind.Disconnected, stage, "rechelper_error_disconnected", error.Message),
            InvalidDataException => new(RecordingFailureKind.Media, stage, "rechelper_error_media", error.Message),
            IOException or UnauthorizedAccessException => new(RecordingFailureKind.Storage, stage, "rechelper_error_io", error.Message),
            _ => new(RecordingFailureKind.Unknown, stage, "rechelper_error_unknown", error.Message)
        };
    }
}
