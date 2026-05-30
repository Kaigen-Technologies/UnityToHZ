using Unity.Entities;

// Custom DOTS bootstrap: create the default world with all the normal systems, but
// do NOT add it to the player loop, so it does not auto-update. FrameTimingDisplay
// steps it manually (world.Update()) once per frame so the ECS update can be timed
// without double-stepping the simulation.
//
// Follows the documented ICustomBootstrap procedure (Entities "Manage systems in
// multiple worlds"): create the World directly, add systems via
// DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups, set
// World.DefaultGameObjectInjectionWorld, return true. We intentionally skip the
// ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop step (that's the
// auto-update hookup). NOTE: do NOT call DefaultWorldInitialization.Initialize here —
// it re-invokes ICustomBootstrap and would recurse infinitely.
public class ManualWorldBootstrap : ICustomBootstrap
{
    public bool Initialize(string defaultWorldName)
    {
        var world = new World(defaultWorldName, WorldFlags.Game);
        World.DefaultGameObjectInjectionWorld = world;

        var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
        DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);

        // No AppendWorldToCurrentPlayerLoop → world only advances via manual Update().
        return true;
    }
}
