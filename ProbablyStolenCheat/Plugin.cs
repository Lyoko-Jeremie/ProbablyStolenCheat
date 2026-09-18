using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace ProbablyStolenCheat;

[BepInPlugin(Plugin.PluginGuid, Plugin.PluginName, Plugin.PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "local.probablystolen.cheat";
    public const string PluginName = "Probably Stolen Cheat";
    public const string PluginVersion = "1.0.0";

    /// <summary>外部 UI 的页面目录（BepInEx/plugins/ps-cheat-web）。改 index.html 即可换界面，无需重新编译。</summary>
    internal static string UiDir { get; private set; } = "ps-cheat-web";

    public override void Load()
    {
        CheatBehaviour.Log = Log;
        ItemEditor.Log = Log;
        SaveServer.Log = Log;

        try
        {
            var asmDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            UiDir = string.IsNullOrEmpty(asmDir) ? "ps-cheat-web" : Path.Combine(asmDir, "ps-cheat-web");
        }
        catch { }

        // IL2CPP 下自定义 MonoBehaviour 必须先注册类型，才能挂到 GameObject 上
        ClassInjector.RegisterTypeInIl2Cpp<CheatBehaviour>();
        AddComponent<CheatBehaviour>();

        // 起本地 HTTP 通道，给外部 UI 用
        SaveServer.Start();

        Log.LogInfo($"{PluginName} v{PluginVersion} 已加载");
        Log.LogInfo("热键：F9 = 修改器面板，F5 = 加 100 万，F8 = 开关物品生成菜单，F10 = 编辑右键指向的物品");
        Log.LogInfo("外部 UI：http://127.0.0.1:8787/  （界面文件：" + UiDir + "）");
    }
}
