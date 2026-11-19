using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.CelesteRecHelper;

internal sealed record RecordingHudView(string Title, string Detail, string Notice, bool Warning);

internal sealed class RecordingHud : Entity
{
    private readonly Func<RecordingHudView?> view;
    public bool WasRemoved { get; private set; }

    public RecordingHud(Func<RecordingHudView?> view)
    {
        this.view = view;
        // Level.UnloadLevel and room transitions remove every non-global entity.
        Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -100000;
    }

    public override void Added(Scene scene)
    {
        WasRemoved = false;
        base.Added(scene);
    }

    public override void Removed(Scene scene)
    {
        WasRemoved = true;
        base.Removed(scene);
    }
    public override void Render()
    {
        var state = view();
        if (state is null) return;
        const float scale = 0.45f;
        var width = Math.Clamp(Math.Max(ActiveFont.Measure(state.Title).X,
            Math.Max(ActiveFont.Measure(state.Detail).X, ActiveFont.Measure(state.Notice).X)) * scale + 30, 480, 1860);
        Draw.Rect(18, 210, width, 96, Color.Black * 0.72f);
        var color = state.Warning ? Color.OrangeRed : Color.White;
        ActiveFont.DrawOutline(state.Title, new Vector2(30, 219), Vector2.Zero, Vector2.One * scale, color, 2, Color.Black);
        ActiveFont.DrawOutline(state.Detail, new Vector2(30, 247), Vector2.Zero, Vector2.One * scale, Color.White, 2, Color.Black);
        ActiveFont.DrawOutline(state.Notice, new Vector2(30, 275), Vector2.Zero, Vector2.One * scale, color, 2, Color.Black);
    }
}
