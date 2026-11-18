using System.Text.Json;
using CelesteRecHelper.Core.Persistence;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("CelesteRecHelper.Worker — stage 1");
    Console.WriteLine("Usage: CelesteRecHelper.Worker routes <Saves/CelesteRecHelper directory>");
    Console.WriteLine("Lists persisted routes without modifying them. Media processing is added in later stages.");
    return 0;
}
if (args is not ["routes", var dataDirectory])
{
    Console.Error.WriteLine("Unknown arguments. Use --help for usage.");
    return 2;
}
try
{
    var store = new ManifestStore(dataDirectory);
    var routes = store.EnumerateRouteIds().Select(store.Load)
        .Select(r => new { r.RouteId, r.MapSid, r.MapName, r.SaveSlot, Status = r.Status.ToString(),
            SegmentCount = r.Segments.Count, MediaCount = r.Media.Count }).ToList();
    Console.WriteLine(JsonSerializer.Serialize(routes, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
