using CelesteRecHelper.Core.Models;

namespace CelesteRecHelper.Core.Recording;

/// <summary>The caller must persist clip references before removing any original replay file.</summary>
public static class SegmentCutFiles
{
    public static int RemoveUnusedReplays(SegmentStateMachine machine, Action<RouteManifest> persist)
    {
        var route = machine.Route;
        var removed = 0;
        foreach (var source in route.Media.Where(media => media.Kind == MediaKind.ReplayBuffer &&
            media.SaveRequestId is not null).ToArray())
        {
            var segments = route.Segments.Where(segment => segment.SaveRequestId == source.SaveRequestId).ToArray();
            if (segments.Length == 0 || segments.Any(segment => segment.SliceStatus != SegmentSliceStatus.Succeeded) ||
                route.Segments.Any(segment => segment.Media.Any(reference => reference.MediaId == source.Id)) ||
                route.CurrentLife?.PendingMedia.Any(reference => reference.MediaId == source.Id) == true)
                continue;
            try { File.Delete(source.FilePath); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            machine.RemoveMedia(source.Id);
            persist(route);
            removed++;
        }
        return removed;
    }
}
