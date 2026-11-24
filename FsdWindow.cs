using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolderThree;

internal sealed class FsdWindow : Window
{
    private readonly FsdApplication _application;
    private readonly YAxisAdjustController _yAxis;
    private readonly CombatRecovery _combatRecovery;
    private readonly ReconnectController _reconnect;
    private readonly Configuration _configuration;
    private readonly DeepDungeonSpeedController _potdSpeed;

    public FsdWindow(FsdApplication application, YAxisAdjustController yAxis,
        CombatRecovery combatRecovery, ReconnectController reconnect,
        Configuration configuration, DeepDungeonSpeedController potdSpeed)
        : base($"你是壞孩子###{ProductIdentity.InternalName}")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));
        _combatRecovery = combatRecovery;
        _reconnect = reconnect;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _potdSpeed = potdSpeed ?? throw new ArgumentNullException(nameof(potdSpeed));
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##FsdHostTabs"))
            return;

        if (ImGui.BeginTabItem("說明"))
        {
            _application.DrawCompanionSettings();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("它也許可以幫助你挖到一些寶藏並成為死靈術士");
            ImGui.TextWrapped("請啟用你所用輸出外掛的非戰鬥時主動攻擊能力.\n請關閉任意其他外掛的目標選擇.\n請關閉任意其他外掛的移動速度修正.");
            ImGui.TextWrapped("死靈術士需要強強PC, 請勿制限低幀");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FSD"))
        {
            if (_reconnect.Status is { } status)
                ImGui.TextWrapped(status);
            if (_reconnect.IsPending && ImGui.Button("取消重連並停止 FSD"))
                _reconnect.Cancel();
            bool reconnecting = _reconnect.IsPending;
            ImGui.BeginDisabled(reconnecting);
            _application.DrawDeepDungeonFormalPanel();
            ImGui.Separator();
            ImGui.Text("通用輔助");
            _application.DrawGeneralAssistantSettings();
            _yAxis.DrawSettings();
            ImGui.EndDisabled();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("深宫移速"))
        {
            DrawPotdSpeedSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("自動嘗試恢復戰鬥循環"))
        {
            _combatRecovery.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("目前可以自動繼續的存檔"))
        {
            _application.DrawProgressionSaves();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawPotdSpeedSettings()
    {
        bool enabled = _configuration.PotdSpeedEnabled;
        bool changed = ImGui.Checkbox("启用移速调整", ref enabled);

        float addition = _configuration.PotdSpeedAddition;
        changed |= ImGui.SliderFloat("移速增加", ref addition, 0f, 5f, "%.2f");

        bool maxAcceleration = _configuration.PotdMaxAcceleration;
        changed |= ImGui.Checkbox("最大加速度", ref maxAcceleration);

        if (changed)
        {
            _configuration.PotdSpeedEnabled = enabled;
            _configuration.PotdSpeedAddition = float.IsFinite(addition)
                ? Math.Clamp(addition, 0f, 5f)
                : 0f;
            _configuration.PotdMaxAcceleration = maxAcceleration;
            _configuration.SavePluginConfig();
        }

        ImGui.Separator();
        ImGui.TextWrapped("移速：死者宫殿 1–200 层、天之宫殿 1–100 层的非整十层生效，并同步用于导航速度估算。");
        ImGui.TextWrapped("2890 拦截：机工士在死者宫殿 141–149、151–159……191–199 层，以及天之宫殿 91–99 层释放时拦截。包含调整后的技能 ID，以及普通和地面目标施放。");
        ImGui.Separator();
        ImGui.TextWrapped(_potdSpeed.Status);
        ImGui.TextDisabled("其他深宫不应用移速调整；所有整十层均排除。");
    }

}
