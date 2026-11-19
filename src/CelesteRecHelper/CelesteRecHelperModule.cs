using CelesteRecHelper.Core.Persistence;

namespace Celeste.Mod.CelesteRecHelper;

public sealed class CelesteRecHelperModule : EverestModule
{
    public static CelesteRecHelperModule Instance { get; private set; } = null!;
    public override Type SettingsType => typeof(CelesteRecHelperModuleSettings);
    public static CelesteRecHelperModuleSettings Settings => (CelesteRecHelperModuleSettings)Instance._Settings;
    public ManifestStore? Manifests { get; private set; }
    public RecoveryReport? Recovery { get; private set; }
    private ObsMenu? obsMenu;
    private GameRecordingAdapter? recording;

    public CelesteRecHelperModule() => Instance = this;

    public override void Load()
    {
        // Recovery is required even when recording is disabled.
        try
        {
            Manifests = new ManifestStore(Path.Combine(Everest.PathSettings, "CelesteRecHelper"));
            Recovery = new StartupRecovery(Manifests).Recover();
            obsMenu = new ObsMenu(this);
            recording = new GameRecordingAdapter(Manifests);
            Logger.Log(LogLevel.Info, "CelesteRecHelper",
                $"Manifest storage initialized. Data: {Manifests.RootDirectory}; interrupted routes: " +
                $"{Recovery.InterruptedRoutes.Count}; recovered saves: {Recovery.RecoveredSaves}; " +
                $"failed saves: {Recovery.FailedSaves}; unfinished jobs: {Recovery.UnfinishedJobs.Count}.");
            foreach (var warning in Recovery.Warnings)
                Logger.Log(LogLevel.Warn, "CelesteRecHelper", warning);
            if (Recovery.OutputsMayStillBeActive)
                Logger.Log(LogLevel.Warn, "CelesteRecHelper", "OBS may still have outputs running from the previous session. Check OBS before starting again.");
            if (Recovery.ObsDirectoryRecovery is not null)
                Logger.Log(LogLevel.Warn, "CelesteRecHelper", "An OBS directory recovery record is pending. Use the OBS connection check to restore it after checking the profile.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Log(LogLevel.Error, "CelesteRecHelper", $"Could not initialize manifest storage: {e.Message}");
        }
    }

    public override void CreateModMenuSection(TextMenu menu, bool inGame, FMOD.Studio.EventInstance snapshot)
    {
        base.CreateModMenuSection(menu, inGame, snapshot);
        obsMenu?.AddTo(menu);
    }

    public override void Unload()
    {
        recording?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        recording = null;
        obsMenu?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        obsMenu = null;
        Recovery = null;
        Manifests = null;
    }
}
