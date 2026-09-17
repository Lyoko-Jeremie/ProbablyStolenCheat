using System;
using BepInEx.Logging;
using UnityEngine;

namespace ProbablyStolenCheat;

/// <summary>
/// 跑在 Unity 主线程的 MonoBehaviour：热键、金钱、调试开关、修改器主面板，
/// 以及最重要的——每帧驱动 <see cref="SaveServer"/> 的主线程任务队列
/// （外部 UI 的 HTTP 请求只能在主线程碰 Unity 对象）。
/// </summary>
public class CheatBehaviour : MonoBehaviour
{
    // Il2CppInterop 要求注入的 MonoBehaviour 提供这个构造函数
    public CheatBehaviour(IntPtr ptr) : base(ptr) { }

    internal static ManualLogSource Log;

    private const int LockAmount = 999_999_999;

    private bool _infiniteMoney;
    private CustomUIWindow _window;

    // ------------------------------------------------------------------ 热键
    private void Update()
    {
        // 把外部 UI 排队的请求在主线程执行完（必须在主线程，Unity 对象非线程安全）
        SaveServer.PumpMainThread();

        if (Input.GetKeyDown(KeyCode.F9)) TogglePanel();                   // 修改器面板
        if (Input.GetKeyDown(KeyCode.F5)) ModCash(1_000_000);              // 加 100 万
        if (Input.GetKeyDown(KeyCode.F10)) ItemEditor.PushContextItem();   // 把右键物品推给外部 UI
        if (_infiniteMoney) LockCash();
    }

    private void OnDestroy()
    {
        _window = null;
        ItemEditor.CloseAll();
        SaveServer.Stop();
    }

    /// <summary>
    /// Il2CppInterop 的 Il2CppSystem.Action 派生自 Il2CppSystem.MulticastDelegate（不是 System.MulticastDelegate），
    /// C# 编译器不把它识别为委托类型，所以 lambda / 方法组要先经过 System.Action 走隐式转换。
    /// </summary>
    private static Il2CppSystem.Action Act(System.Action a) => a;
    private static Il2CppSystem.Action<T> Act<T>(System.Action<T> a) => a;

    // ------------------------------------------------------- PlayerStore 访问
    private static PlayerStore Store()
    {
        try
        {
            if (!PlayerStore.IsInstanceExist()) return null;
            return PlayerStore.Instance;
        }
        catch (Exception e)
        {
            Log?.LogError("获取 PlayerStore 失败: " + e);
            return null;
        }
    }

    // ---------------------------------------------------------------- 金钱
    private void LockCash()
    {
        var ps = Store();
        if (ps != null && ps.playerCash < LockAmount) ps.playerCash = LockAmount;
    }

    internal void ModCash(int delta)
    {
        var ps = Store();
        if (ps == null) { Log?.LogWarning("PlayerStore 尚未创建（可能还没进入游戏）"); return; }
        ps.ModCash(delta, true);
        Log?.LogInfo($"ModCash({delta}) -> playerCash = {ps.playerCash}");
    }

    internal void SetCash(int amount)
    {
        var ps = Store();
        if (ps == null) { Log?.LogWarning("PlayerStore 尚未创建"); return; }
        ps.playerCash = amount;
        ps.isProduction = false; // 顺手解开调试入口的锁
        Log?.LogInfo($"playerCash = {amount}（isProduction 已置 false）");
    }

    internal void ClearMainInv()
    {
        try
        {
            DebugPanel.ClearMainInv();
            Log?.LogInfo("已清空主库存");
        }
        catch (Exception e) { Log?.LogError("ClearMainInv 失败: " + e); }
    }

    // ---------------------------------------------------------------- 调试
    internal bool GetDevMode()
    {
        var ps = Store();
        return ps != null && ps.devMode;
    }

    internal void SetDevMode(bool on)
    {
        var ps = Store();
        if (ps == null) { Log?.LogWarning("PlayerStore 尚未创建"); return; }
        ps.isProduction = false;   // isProduction 会掐断 devMode 的官方按键路径
        ps.devMode = on;
        Log?.LogInfo($"devMode = {on}（isProduction 已置 false）");
    }

    internal void OpenSpawnMenu()
    {
        var dh = DebugHandler.current;
        if (dh == null) { Log?.LogWarning("DebugHandler.current 为 null"); return; }
        dh.ToggleSpawnMenu();
        Log?.LogInfo("已请求打开游戏自带的物品生成菜单");
    }

    // -------------------------------------------------------------- 存读档
    internal void DoSave()
    {
        try
        {
            SaveManager.Save();
            Log?.LogInfo("已调用 SaveManager.Save() 让游戏自己写档");
        }
        catch (Exception e) { Log?.LogError("SaveManager.Save() 失败: " + e); }
    }

    internal void DoLoad()
    {
        try
        {
            SaveManager.Load();
            Log?.LogInfo("已调用 SaveManager.Load() 让游戏自己读档");
        }
        catch (Exception e) { Log?.LogError("SaveManager.Load() 失败: " + e); }
    }

    // ---------------------------------------------------------------- 主面板
    internal void TogglePanel()
    {
        try
        {
            if (_window != null && _window.IsAlive)
            {
                _window.Close();
                _window = null;
                Log?.LogInfo("修改器面板已关闭");
                return;
            }
            BuildPanel();
        }
        catch (Exception e) { Log?.LogError("切换面板失败: " + e); }
    }

    private void BuildPanel()
    {
        var mgr = CustomUIManager.Instance;
        if (mgr == null)
        {
            Log?.LogWarning("CustomUIManager.Instance 为 null —— 面板只能在商店场景里打开");
            return;
        }

        var b = mgr.CreateWindow("ps.cheat.panel", "修改器", CustomUIManager.LAYER_OVERLAY)
                   .SetSize(520f, 640f)
                   .Center()
                   .SetDraggable(true)
                   .SetCloseOnEscape(true);

        b.AddLabel("── 金钱 ──", "lbl_money");
        b.AddButton("+100 万", Act(() => ModCash(1_000_000)), "btn_m1");
        b.AddButton("+1000 万", Act(() => ModCash(10_000_000)), "btn_m2");
        b.AddInput("金额，回车应用", "1000000", Act<string>(s =>
        {
            if (int.TryParse(s, out var v)) SetCash(v);
            else Log?.LogWarning("无法解析金额: " + s);
        }), "inp_money");
        b.AddToggle("无限金钱锁定", _infiniteMoney, Act<bool>(v =>
        {
            _infiniteMoney = v;
            Log?.LogInfo("无限金钱 = " + v);
        }), "tgl_infmoney");

        b.AddLabel("── 物品 ──", "lbl_item");
        b.AddButton("★ 在外部 UI 打开右键物品（F10）", Act(ItemEditor.PushContextItem), "btn_ctx");
        b.AddButton("★ 物品树（选物品 → 推送到外部 UI）", Act(ItemEditor.ToggleTree), "btn_editor");
        b.AddButton("全部物品价值拉满（含容器内）", Act(ItemEditor.BoostAllItems), "btn_boost");
        b.AddButton("全部物品：清除品质惩罚（含容器内）", Act(ItemEditor.ClearAllPenalties), "btn_penalty");
        b.AddButton("清空主库存", Act(ClearMainInv), "btn_clearinv");

        b.AddLabel("── 存读档（走游戏自己的 SaveManager） ──", "lbl_save");
        b.AddButton("立即保存存档", Act(DoSave), "btn_save");
        b.AddButton("重新读取存档", Act(DoLoad), "btn_load");

        b.AddLabel("── 调试 ──", "lbl_debug");
        b.AddToggle("devMode", GetDevMode(), Act<bool>(SetDevMode), "tgl_dev");
        b.AddButton("打开物品生成菜单", Act(OpenSpawnMenu), "btn_spawn");

        b.AddLabel("热键：F9 面板 / F5 加钱 / F10 外部 UI 打开右键物品", "lbl_hint");
        b.AddLabel("外部 UI（物品属性在这里看）：http://127.0.0.1:8787/", "lbl_ui");

        _window = b.Show();
        Log?.LogInfo("修改器面板已创建");
    }
}
