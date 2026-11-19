namespace CelesteRecHelper.Core.Models;

/// <summary>Stable game identity used to decide whether a new Session continues the same route.</summary>
public readonly record struct RouteIdentity(int SaveSlot, string MapSid, int AreaMode);