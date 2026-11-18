using Celeste.Mod.UI;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.CelesteRecHelper;

[SettingName("modoptions_celesterechelper_title")]
public sealed class CelesteRecHelperModuleSettings : EverestModuleSettings
{
    [SettingName("modoptions_celesterechelper_enabled")]
    public bool Enabled { get; set; }

    [SettingName("modoptions_celesterechelper_endsegment")]
    [DefaultButtonBinding((Buttons)0, Keys.F8)]
    public ButtonBinding EndSegment { get; set; } = new();

    [SettingName("modoptions_celesterechelper_undo")]
    [DefaultButtonBinding((Buttons)0, Keys.F9)]
    public ButtonBinding Undo { get; set; } = new();

    [SettingName("modoptions_celesterechelper_obshost")]
    [SettingMaxLength(255)]
    public string ObsHost { get; set; } = "127.0.0.1";

    [SettingName("modoptions_celesterechelper_obsport")]
    [SettingRange(1, 65535, true)]
    [SettingNumberInput(false, 5)]
    public int ObsPort { get; set; } = 4455;

    [SettingName("modoptions_celesterechelper_obspassword")]
    [SettingMaxLength(256)]
    [SettingMinLength(0)]
    public string ObsPassword { get; set; } = "";

    // The menu masks the value. Editing uses Everest's standard string editor.
    public void CreateObsPasswordEntry(TextMenu menu, bool inGame)
    {
        if (inGame)
            return;
        var display = ObsPassword.Length == 0 ? "-" : new string('*', Math.Min(ObsPassword.Length, 12));
        menu.Add(new TextMenu.Button(Dialog.Clean("modoptions_celesterechelper_obspassword") + ": " + display)
            .Pressed(() => menu.SceneAs<Overworld>().Goto<OuiModOptionString>().Init<OuiModOptions>(
                ObsPassword, value => ObsPassword = value, 256, 0)));
    }

    [SettingName("modoptions_celesterechelper_outputdirectory")]
    [SettingSubText("modoptions_celesterechelper_outputdirectory_help")]
    [SettingMaxLength(1024)]
    [SettingMinLength(0)]
    public string OutputDirectory { get; set; } = "";
}
