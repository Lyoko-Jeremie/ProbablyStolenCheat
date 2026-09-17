using System;
using BepInEx.Logging;
using UnityEngine;

namespace ProbablyStolenCheat;

/// <summary>
/// 物品编辑器：树状物品列表（支持容器多层嵌套）+ 树状属性编辑（每项都带中文名与英文字段名）。
///
/// UI 用的是游戏自带 CustomUI 框架。它的能力边界（已核实）：
///   · 支持嵌套布局：BeginColumn / BeginRow / BeginGrid / BeginScroll + End()（栈式关闭）
///   · 没有原生 TreeView，也没有"删除/清空已生成控件"的接口（只有 Get(id).SetXxx 改值）
///   → 所以「展开/折叠」的做法是：切换状态后关窗重建，只构建可见节点（懒展开）。
/// </summary>
internal static class ItemEditor
{
    internal static ManualLogSource Log;

    private const int MaxDepth = 5;    // 容器递归深度上限
    private const int MaxRows = 260;   // 一次最多渲染多少行，避免重建卡顿

    private static CustomUIWindow _tree;
    private static CustomUIWindow _props;

    private static string _filter = "";
    private static GameItem _sel;
    private static string _selPath = "-";
    private static int _rows;

    private static readonly System.Collections.Generic.HashSet<int> _openItems = new System.Collections.Generic.HashSet<int>();
    private static readonly System.Collections.Generic.HashSet<string> _openFeats = new System.Collections.Generic.HashSet<string>();
    private static readonly System.Collections.Generic.Dictionary<string, string> _pending = new System.Collections.Generic.Dictionary<string, string>();

    private static Il2CppSystem.Action Act(System.Action a) => a;
    private static Il2CppSystem.Action<T> Act<T>(System.Action<T> a) => a;

    // ================================================================ 入口
    internal static void ToggleTree()
    {
        try
        {
            if (_tree != null && _tree.IsAlive) { CloseAll(); return; }
            BuildTree();
        }
        catch (Exception e) { Log?.LogError("打开物品树失败: " + e); }
    }

    /// <summary>
    /// 把游戏右键菜单当前指向的物品推送到外部 UI（ItemContextHandler.currentItem）。
    /// 游戏内的 CustomUI 没有滚动容器，属性一多就画不下，所以完整属性一律放外部界面看。
    /// </summary>
    internal static void PushContextItem()
    {
        try
        {
            GameItem item = null;
            try
            {
                var ctx = ItemContextHandler.current;
                if (ctx != null) item = ctx.currentItem;
            }
            catch (Exception e) { Log?.LogWarning("读取 ItemContextHandler 失败: " + e.Message); }

            if (item == null)
            {
                Log?.LogWarning("没有正在右键的物品：请先把鼠标移到物品上按右键，再按 F10");
                return;
            }

            _sel = item;
            _selPath = GuessPath(item);
            SaveServer.FocusItem(Uid(item));

            Log?.LogInfo($"已把「{item.identifier} / {item.name}」(uniqueId={Uid(item)}) 推送到外部 UI；" +
                         "浏览器打开 http://127.0.0.1:8787/ 即可看到（若已打开会自动跳过去）");
        }
        catch (Exception e) { Log?.LogError("推送到外部 UI 失败: " + e); }
    }

    /// <summary>把当前选中的物品推送给外部 UI。</summary>
    internal static void PushSelectedItem()
    {
        if (_sel == null) { Log?.LogWarning("还没选中物品"); return; }
        SaveServer.FocusItem(Uid(_sel));
        Log?.LogInfo($"已把 {_sel.identifier} 推送到外部 UI");
    }

    internal static void CloseAll()
    {
        try { if (_tree != null && _tree.IsAlive) _tree.Close(); } catch { }
        try { if (_props != null && _props.IsAlive) _props.Close(); } catch { }
        _tree = null;
        _props = null;
    }

    // ============================================================ 库存访问
    private static PlayerStore Store()
    {
        try
        {
            if (!PlayerStore.IsInstanceExist()) return null;
            return PlayerStore.Instance;
        }
        catch { return null; }
    }

    private static EmporiumEntry Emporium()
    {
        try { return EmporiumEntry._Instance_k__BackingField; }
        catch { return null; }
    }

    private static GameGridInventory MainInventory()
    {
        var emp = Emporium();
        if (emp != null)
        {
            var inv = emp._invElement_k__BackingField;
            if (inv != null) return inv;
        }
        var ps = Store();
        return ps?.gridInv;
    }

    private static string GuessPath(GameItem item)
    {
        try
        {
            var inv = item.parentInventory;
            if (inv != null && !string.IsNullOrEmpty(inv.identifier)) return inv.identifier;
        }
        catch { }
        return "（右键物品）";
    }

    private static int Uid(GameItem item)
    {
        try { return item.uniqueId; } catch { return 0; }
    }

    /// <summary>把 8 个根库存各渲染一遍（主库存 + EmporiumEntry 的其余库存）。</summary>
    private static void RenderAllRoots(CustomUIBuilder b)
    {
        var emp = Emporium();
        RenderInventory(b, MainInventory(), "主库存", 0);
        if (emp == null) return;
        RenderInventory(b, emp._showcaseElement_k__BackingField, "展示柜", 0);
        RenderInventory(b, emp._frontInvinvElement_k__BackingField, "前背包", 0);
        RenderInventory(b, emp._backInvinvElement_k__BackingField, "后背包", 0);
        RenderInventory(b, emp._hiddenElement_k__BackingField, "隐藏区", 0);
        RenderInventory(b, emp._bazarLeftinvElement_k__BackingField, "集市左侧", 0);
        RenderInventory(b, emp._trashInvElement_k__BackingField, "垃圾桶", 0);
        RenderInventory(b, emp._docInvElement_k__BackingField, "文件袋", 0);
    }

    // ============================================================== 物品树
    private static void BuildTree()
    {
        var mgr = CustomUIManager.Instance;
        if (mgr == null)
        {
            Log?.LogWarning("CustomUIManager.Instance 为 null —— 物品树只能在商店场景里打开");
            return;
        }

        _rows = 0;

        var b = mgr.CreateWindow("ps.cheat.tree", "物品树", CustomUIManager.LAYER_OVERLAY)
                   .SetSize(740f, 780f)
                   .Center()
                   .SetDraggable(true)
                   .SetCloseOnEscape(true);

        b.AddLabel("▶/▼ = 展开或折叠容器；点物品行 = 把该物品推送到外部 UI 查看/修改", "t_hint");
        b.AddInput("筛选：identifier / 名字（有筛选时自动全展开）", _filter, Act<string>(s => _filter = s), "t_filter");
        b.BeginRow(6f);
        b.AddButton("应用筛选 / 刷新", Act(RefreshTree), "t_refresh");
        b.AddButton("全部折叠", Act(CollapseAll), "t_collapse");
        b.AddButton("在外部 UI 打开右键物品（F10）", Act(PushContextItem), "t_ctx");
        b.End();
        b.AddLabel("外部 UI 地址：http://127.0.0.1:8787/", "t_ui");

        b.BeginScroll(490f);
        RenderAllRoots(b);
        if (_rows == 0) b.AddLabel("（没有匹配的物品；清空筛选后再刷新）", "t_empty");
        b.End();

        _tree = b.Show();
        Log?.LogInfo($"物品树已构建：渲染 {_rows} 行（筛选「{_filter}」）");
    }

    private static void RefreshTree()
    {
        try
        {
            if (_tree != null && _tree.IsAlive) _tree.Close();
            _tree = null;
            BuildTree();
        }
        catch (Exception e) { Log?.LogError("重建物品树失败: " + e); }
    }

    private static void CollapseAll()
    {
        _openItems.Clear();
        _openFeats.Clear();
        RefreshTree();
        Log?.LogInfo("物品树：已全部折叠");
    }

    private static bool IsFiltering()
    {
        return _filter != null && _filter.Trim().Length > 0;
    }

    private static bool Match(string identifier, string name, string path)
    {
        string f = _filter == null ? "" : _filter.Trim();
        if (f.Length == 0) return true;
        if (identifier == null) identifier = "";
        if (name == null) name = "";
        if (path == null) path = "";
        return identifier.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>该物品本身、或它容器里的任意后代命中筛选词。</summary>
    private static bool MatchesDeep(GameItem item, int depth)
    {
        if (item == null) return false;
        if (Match(item.identifier, item.name, null)) return true;
        if (depth >= MaxDepth) return false;

        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = item.children; } catch { return false; }
        if (children == null) return false;

        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;

            GameItem sub = null;
            try { sub = ch.TryCast<GameItem>(); } catch { }
            if (sub != null && MatchesDeep(sub, depth + 1)) return true;

            GameInventory subInv = null;
            try { subInv = ch.TryCast<GameInventory>(); } catch { }
            if (subInv != null && MatchesInventoryDeep(subInv, depth + 1)) return true;
        }
        return false;
    }

    private static bool MatchesInventoryDeep(GameInventory inv, int depth)
    {
        if (inv == null || depth > MaxDepth) return false;

        Il2CppSystem.Collections.Generic.List<GameItem> items;
        try { items = inv.childItems; } catch { return false; }
        if (items == null) return false;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            if (Match(it.identifier, it.name, null)) return true;
            if (MatchesDeep(it, depth)) return true;
        }
        return false;
    }

    private static void RenderInventory(CustomUIBuilder b, GameInventory inv, string path, int depth)
    {
        if (inv == null || depth > MaxDepth || _rows >= MaxRows) return;

        Il2CppSystem.Collections.Generic.List<GameItem> items;
        try { items = inv.childItems; }
        catch (Exception e) { Log?.LogWarning($"读取「{path}」物品列表失败: {e.Message}"); return; }
        if (items == null) return;

        for (int i = 0; i < items.Count && _rows < MaxRows; i++)
        {
            var it = items[i];
            if (it == null) continue;
            RenderItem(b, it, path, depth);
        }
    }

    private static void RenderItem(CustomUIBuilder b, GameItem item, string path, int depth)
    {
        if (item == null || _rows >= MaxRows) return;

        bool filtering = IsFiltering();
        if (filtering && !MatchesDeep(item, 0)) return;   // 自己与所有后代都不匹配 → 整枝跳过

        int uid = Uid(item);
        string id = item.identifier ?? "?";
        string nm = item.name ?? "";

        // 同样不能用 children.Count（里面通常只有一个 PixelWindow 中间节点），要递归数真实物品数
        int childCount = CountInner(item, depth + 1, 0);
        bool isContainer = childCount > 0;
        bool open = filtering || _openItems.Contains(uid);  // 有筛选时自动全展开，方便看命中项

        string indent = new string(' ', depth * 2);
        string mark = isContainer ? (open ? "▼" : "▶") : "·";
        string suffix = isContainer ? $"  （内含 {childCount} 件）" : "";
        string text = $"{indent}{mark} {id} | {nm} | v={item.unitValue}{suffix}";

        int rowId = _rows;
        b.AddButton(text, Act(() => OnItemClick(item, path, isContainer)), $"n_{rowId}");
        _rows++;

        // 无条件下钻：children 里可能是 PixelWindow 这类中间节点，它再往下才是库存
        if (open && depth < MaxDepth)
            RenderChildren(b, item, path + "/" + id, depth + 1);
    }

    private static void RenderChildren(CustomUIBuilder b, GameItem item, string childPath, int depth)
    {
        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = item.children; }
        catch (Exception e) { Log?.LogWarning($"读取「{item.identifier}」子节点失败: {e.Message}"); return; }
        if (children == null) return;

        for (int i = 0; i < children.Count && _rows < MaxRows; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            RenderNode(b, ch, childPath, depth, 0);
        }
    }

    /// <summary>
    /// 递归下钻一个 GraphNodeStorage：可能是物品、库存，也可能是夹在中间的窗口节点
    /// （PixelWindow 实现了 GraphNodeWindow 接口）。中间节点不算一层，继续往下钻，
    /// 否则"窗口下挂库存"的物品在树里会整棵消失。
    /// </summary>
    private static void RenderNode(CustomUIBuilder b, GraphNodeStorage node, string path, int depth, int hops)
    {
        if (node == null || depth > MaxDepth || hops > 8 || _rows >= MaxRows) return;

        GameItem item = null;
        try { item = node.TryCast<GameItem>(); } catch { }
        if (item != null) { RenderItem(b, item, path, depth); return; }

        GameInventory inv = null;
        try { inv = node.TryCast<GameInventory>(); } catch { }
        if (inv != null) { RenderInventory(b, inv, path, depth); return; }

        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = node.children; } catch { return; }
        if (children == null) return;

        for (int i = 0; i < children.Count && _rows < MaxRows; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            RenderNode(b, ch, path, depth, hops + 1);
        }
    }

    private static void OnItemClick(GameItem item, string path, bool isContainer)
    {
        try
        {
            int uid = Uid(item);
            if (isContainer)
            {
                if (!_openItems.Add(uid)) _openItems.Remove(uid);   // 切换展开状态
            }

            _sel = item;
            _selPath = path + "/" + (item.identifier ?? "?");
            SaveServer.FocusItem(uid);   // 完整属性放外部 UI 看（游戏内画不下）

            RefreshTree();
            Log?.LogInfo($"已把 {item.identifier}（{_selPath}）推送到外部 UI");
        }
        catch (Exception e) { Log?.LogError("点击物品失败: " + e); }
    }

    // ============================================================ 属性窗口
    private static void CloseProps()
    {
        try { if (_props != null && _props.IsAlive) _props.Close(); } catch { }
        _props = null;
    }

    private static void BuildProperties()
    {
        var mgr = CustomUIManager.Instance;
        if (mgr == null || _sel == null) return;

        CloseProps();

        var b = mgr.CreateWindow("ps.cheat.props", "物品属性", CustomUIManager.LAYER_OVERLAY)
                   .SetSize(660f, 760f)
                   .Center()
                   .SetDraggable(true)
                   .SetCloseOnEscape(true);

        b.AddLabel($"对象：{_selPath}", "p_hdr");
        b.AddLabel("格式：中文名 英文字段名 —— 输入框改完点下面「应用」", "p_hint");
        b.AddButton("应用本页修改（输入框里的值）", Act(ApplyPending), "p_apply");

        // ---------------- 分组 1：基本属性
        b.AddLabel("════ 基本属性（GameItem）════", "g_basic");
        PropRead(b, "物品ID", "identifier", () => _sel.identifier);
        PropEdit(b, "名称", "name", () => _sel.name);
        PropEdit(b, "短描述", "shortDescription", () => _sel.shortDescription);
        PropEdit(b, "长描述", "longDescription", () => _sel.longDescription);
        PropEdit(b, "风味文本", "flavorText", () => _sel.flavorText);
        PropEdit(b, "自定义文本", "customText", () => _sel.customText);
        PropRead(b, "唯一ID", "uniqueId", () => _sel.uniqueId.ToString());
        PropEdit(b, "数量", "unitCount", () => _sel.unitCount.ToString());
        PropEdit(b, "价值", "unitValue", () => _sel.unitValue.ToString());
        PropEdit(b, "基础价值", "unitBaseValue", () => _sel.unitBaseValue.ToString());
        PropEdit(b, "后期价值", "lateUnitValue", () => _sel.lateUnitValue.ToString());
        PropEdit(b, "备份价值", "backupUnitValue", () => _sel.backupUnitValue.ToString());
        PropRead(b, "物品类型", "itemTypes", () => JoinStrings(_sel.itemTypes));
        PropRead(b, "图标路径", "spritePath", () => _sel.spritePath);
        PropToggle(b, "禁用激活", "forceDisableActivate", () => _sel.forceDisableActivate, v => _sel.forceDisableActivate = v);
        PropToggle(b, "禁用使用", "forceDisableUse", () => _sel.forceDisableUse, v => _sel.forceDisableUse = v);
        PropToggle(b, "默认激活", "activateDefault", () => _sel.activateDefault, v => _sel.activateDefault = v);

        // ---------------- 分组 2：特征（可折叠的子树）
        Il2CppSystem.Collections.Generic.List<ItemFeature> feats = null;
        try { feats = _sel.itemFeatures; } catch { }
        int featCount = feats == null ? 0 : feats.Count;
        b.AddLabel($"════ 特征 itemFeatures（{featCount} 条）════", "g_feat");

        if (feats != null)
        {
            for (int i = 0; i < featCount; i++)
            {
                ItemFeature f;
                try { f = feats[i]; } catch { continue; }
                if (f == null) continue;

                string key = Uid(_sel) + "_" + i;
                bool open = _openFeats.Contains(key);
                string head = $"  {(open ? "▼" : "▶")} 特征[{i}] {SafeEnum(() => f.featureType.ToString())}" +
                              $"（valueModifier={SafeInt(() => f.valueModifier)}）";

                b.AddButton(head, Act(() => ToggleFeature(key)), $"f_{i}_hdr");

                if (!open) continue;

                PropRead(b, "    特征类型", $"feat_{i}_featureType", () => SafeEnum(() => f.featureType.ToString()));
                PropRead(b, "    价值档位", $"feat_{i}_valueStage", () => SafeEnum(() => f.valueStage.ToString()));
                PropRead(b, "    关联物品ID", $"feat_{i}_parentItemUniqueId", () => SafeInt(() => f.parentItemUniqueId).ToString());
                PropRead(b, "    标识", $"feat_{i}_identifier", () => f.identifier);
                PropRead(b, "    分类", $"feat_{i}_category", () => f.category);
                PropEdit(b, "    价值修正", $"feat_{i}_valueModifier", () => SafeInt(() => f.valueModifier).ToString());
                PropEdit(b, "    暴露前修正", $"feat_{i}_preExposeValueModifier", () => SafeInt(() => f.preExposeValueModifier).ToString());
                PropEdit(b, "    公开显示", $"feat_{i}_publicDisplay", () => f.publicDisplay);
                PropEdit(b, "    真实显示", $"feat_{i}_actualDisplay", () => f.actualDisplay);
                PropEdit(b, "    移除工具", $"feat_{i}_removedByTool", () => f.removedByTool);
                PropEdit(b, "    自定义值", $"feat_{i}_customIntValue1", () => SafeInt(() => f.customIntValue1).ToString());
                PropToggle(b, "    已暴露", $"feat_{i}_isFeatureExposed", () => SafeBool(() => f.isFeatureExposed), v => f.isFeatureExposed = v);
                PropToggle(b, "    已发现", $"feat_{i}_isFeatureDiscovered", () => SafeBool(() => f.isFeatureDiscovered), v => f.isFeatureDiscovered = v);
                PropToggle(b, "    对玩家隐藏", $"feat_{i}_isPublicHidden", () => SafeBool(() => f.isPublicHidden), v => f.isPublicHidden = v);
                PropToggle(b, "    可暴露", $"feat_{i}_isExposable", () => SafeBool(() => f.isExposable), v => f.isExposable = v);
                PropToggle(b, "    已禁用", $"feat_{i}_isDisabled", () => SafeBool(() => f.isDisabled), v => f.isDisabled = v);
                PropToggle(b, "    使用状态", $"feat_{i}_useCondition", () => SafeBool(() => f.useCondition), v => f.useCondition = v);
            }
        }

        // ---------------- 分组 3：状态标记（只读）
        b.AddLabel("════ 状态标记（只读）════", "g_state");
        PropRead(b, "战斗背包", "isCombatBackpack", () => SafeBool(() => _sel.isCombatBackpack).ToString());
        PropRead(b, "战斗外可用", "canUseOutsideCombat", () => SafeBool(() => _sel.canUseOutsideCombat).ToString());
        PropRead(b, "调试菜单物品", "isDebugMenu", () => SafeBool(() => _sel.isDebugMenu).ToString());
        PropRead(b, "使用MOD图标", "useSpriteFromMod", () => SafeBool(() => _sel.useSpriteFromMod).ToString());
        PropRead(b, "图标已替换", "spriteChanged", () => SafeBool(() => _sel.spriteChanged).ToString());

        b.AddButton("该物品：价值惩罚 valueModifier 归零", Act(() => BulkFeature(f => { f.valueModifier = 0; f.preExposeValueModifier = 0; }, "valueModifier 归零")), "p_zero");
        b.AddButton("该物品：特征全部标记为已发现/不隐藏", Act(() => BulkFeature(f => { f.isFeatureDiscovered = true; f.isFeatureExposed = true; f.isPublicHidden = false; }, "标记已发现")), "p_disc");

        _props = b.Show();
    }

    private static void ToggleFeature(string key)
    {
        if (!_openFeats.Add(key)) _openFeats.Remove(key);
        BuildProperties();   // 重建以更新折叠状态
    }

    // -------------------------------------------------- 属性行的小工具
    private static void PropRead(CustomUIBuilder b, string label, string field, Func<string> get)
    {
        string v;
        try { v = get() ?? ""; } catch { v = "（读取失败）"; }
        b.AddLabel($"• {label}  [{field}] = {v}", $"r_{field}");
    }

    private static void PropEdit(CustomUIBuilder b, string label, string field, Func<string> get)
    {
        string v;
        try { v = get() ?? ""; } catch { v = ""; }
        b.AddLabel($"• {label}  [{field}]", $"l_{field}");
        b.AddInput("", v, Act<string>(s => _pending[field] = s), $"e_{field}");
    }

    private static void PropToggle(CustomUIBuilder b, string label, string field, Func<bool> get, Action<bool> set)
    {
        bool v = false;
        try { v = get(); } catch { }
        b.AddToggle($"• {label}  [{field}]", v, Act<bool>(on =>
        {
            try { set(on); Log?.LogInfo($"{label} {field} -> {on}"); }
            catch (Exception e) { Log?.LogWarning($"{label} 设置失败: {e.Message}"); }
        }), $"t_{field}");
    }

    // ---------------------------------------------------------- 应用修改
    private static void ApplyPending()
    {
        if (_sel == null) { Log?.LogWarning("没有选中物品"); return; }

        int n = 0;
        foreach (var kv in _pending)
        {
            try { if (ApplyOne(kv.Key, kv.Value)) n++; }
            catch (Exception e) { Log?.LogWarning($"应用 {kv.Key} 失败: {e.Message}"); }
        }
        _pending.Clear();
        Log?.LogInfo($"已应用 {n} 项修改到 {_sel.identifier}");
        BuildProperties();
    }

    private static bool ApplyOne(string field, string val)
    {
        // 特征属性：feat_<index>_<字段名>
        if (field.StartsWith("feat_"))
        {
            var parts = field.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[1], out var idx))
            {
                var feats = _sel.itemFeatures;
                if (feats == null || idx >= feats.Count) return false;
                var f = feats[idx];
                if (f == null) return false;

                switch (parts[2])
                {
                    case "valueModifier": if (int.TryParse(val, out var vm)) { f.valueModifier = vm; return true; } return false;
                    case "preExposeValueModifier": if (int.TryParse(val, out var pv)) { f.preExposeValueModifier = pv; return true; } return false;
                    case "customIntValue1": if (int.TryParse(val, out var ci)) { f.customIntValue1 = ci; return true; } return false;
                    case "publicDisplay": f.publicDisplay = val; return true;
                    case "actualDisplay": f.actualDisplay = val; return true;
                    case "removedByTool": f.removedByTool = val; return true;
                }
            }
            return false;
        }

        switch (field)
        {
            case "name": _sel.name = val; return true;
            case "shortDescription": _sel.shortDescription = val; return true;
            case "longDescription": _sel.longDescription = val; return true;
            case "flavorText": _sel.flavorText = val; return true;
            case "customText": _sel.customText = val; return true;
            case "unitCount": if (int.TryParse(val, out var c)) { _sel.unitCount = c; return true; } return false;
            case "unitValue": if (long.TryParse(val, out var v)) { _sel.unitValue = v; return true; } return false;
            case "unitBaseValue": if (long.TryParse(val, out var bv)) { _sel.unitBaseValue = bv; return true; } return false;
            case "lateUnitValue": if (long.TryParse(val, out var lv)) { _sel.lateUnitValue = lv; return true; } return false;
            case "backupUnitValue": if (long.TryParse(val, out var uv)) { _sel.backupUnitValue = uv; return true; } return false;
        }
        return false;
    }

    private static void BulkFeature(Action<ItemFeature> act, string what)
    {
        if (_sel == null) return;

        Il2CppSystem.Collections.Generic.List<ItemFeature> feats = null;
        try { feats = _sel.itemFeatures; } catch { }
        if (feats == null) { Log?.LogInfo("该物品没有特征"); return; }

        int n = 0;
        for (int i = 0; i < feats.Count; i++)
        {
            var f = feats[i];
            if (f == null) continue;
            try { act(f); n++; } catch { }
        }
        Log?.LogInfo($"{_sel.identifier}：{n} 条特征已{what}");
        BuildProperties();
    }

    // ---------------------------------------------------------- 批量操作
    internal static void BoostAllItems()
    {
        int n = 0;
        ForEachItem(item =>
        {
            try { item.unitValue = 99_999L; item.unitBaseValue = 99_999L; n++; } catch { }
        });
        Log?.LogInfo($"已把 {n} 件物品（含容器内）的 unitValue / unitBaseValue 设为 99999");
    }

    internal static void ClearAllPenalties()
    {
        int n = 0, touched = 0;
        ForEachItem(item =>
        {
            Il2CppSystem.Collections.Generic.List<ItemFeature> feats = null;
            try { feats = item.itemFeatures; } catch { return; }
            if (feats == null) return;

            bool any = false;
            for (int j = 0; j < feats.Count; j++)
            {
                var f = feats[j];
                if (f == null) continue;
                f.valueModifier = 0;
                if (f.actualDisplay != null) f.publicDisplay = f.actualDisplay;
                n++; any = true;
            }
            if (any) touched++;
        });
        Log?.LogInfo($"已处理 {touched} 件物品的 {n} 条特征（含容器内；valueModifier 归零）");
    }

    /// <summary>右键菜单当前指向的物品所在的库存（未必属于下面任何一根）。</summary>
    private static GameInventory ContextInventory()
    {
        try
        {
            var ctx = ItemContextHandler.current;
            if (ctx != null) return ctx.currentInventory;
        }
        catch { }
        return null;
    }

    /// <summary>遍历所有根库存下的物品，含容器内部（深度受限、uniqueId 防环）。</summary>
    private static void ForEachItem(Action<GameItem> act)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        WalkInventory(MainInventory(), seen, 0, act);
        WalkInventory(ContextInventory(), seen, 0, act);   // 右键物品所在库存

        var emp = Emporium();
        if (emp == null) return;
        WalkInventory(emp._showcaseElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._frontInvinvElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._backInvinvElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._hiddenElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._bazarLeftinvElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._trashInvElement_k__BackingField, seen, 0, act);
        WalkInventory(emp._docInvElement_k__BackingField, seen, 0, act);
    }

    /* ------------------------------------------------------------ 快速作弊
       批量功能，全部只作用于 modifiedState（游戏把"当前实际数值"记在这里）。 */

    private const string BatteryMaxTag = "power_source_item_max_energy";
    private const string BatteryCurTag = "power_source_item_energy";

    private static readonly string[] WaterPartTags = {
        "CURRENT_PART_HEAVY_METAL",
        "CURRENT_PART_ORGANIC_WASTE",
        "CURRENT_PART_MICROPLASTIC",
        "CURRENT_PART_MICROBE",
        "CURRENT_PART_CHEMICAL_CONTAMINANT",
        "CURRENT_PART_PHYSICAL_CONTAMINANT",
        "CURRENT_PART_MINERAL"
    };

    /// <summary>
    /// 只取"已经存在"的标签，不存在返回 null。
    ///
    /// 不能直接用 TagSystem.GetTag：它在标签不存在时会顺手新建一个空标签塞进 dict，
    /// 于是调用方永远拿不到 null，"没有该属性就跳过"的规则会静默失效——
    /// 批量遍历时就表现为"所有物品都被写上了这个属性"。
    /// 所以这里先查 dict 判断存在性，确认存在后再取。
    /// </summary>
    private static TagState GetExistingTag(TagSystem ts, string name)
    {
        if (ts == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var dict = ts.dict;
            if (dict == null || !dict.ContainsKey(name)) return null;
            return dict[name];
        }
        catch { return null; }
    }

    /// <summary>
    /// 快速作弊入口。
    ///   "battery" —— 同时有能量上限与当前电量的物品，把当前电量写成上限（充满）。
    ///   "water"   —— 含水位污染标记的物品，把这些标记清零（净化）。
    /// strictWater: water 专用。true = 必须 7 个 CURRENT_PART_* 标记全在才处理；
    ///              false = 只要带其中任意一个，就把它含有的那些清零。
    ///
    /// 与收藏夹同一套规则：只操作"已经存在"的标签（GetExistingTag），取不到就跳过——
    /// 绝不新建、不补默认值。所以物品没有该属性时，改动不会落到它身上。
    /// 只读写 modifiedState；只写 valueInt，不碰 valueEnabled。
    /// </summary>
    public static string DoQuick(string action, bool strictWater)
    {
        int scanned = 0, eligible = 0, changed = 0, tags = 0;

        ForEachItem(it =>
        {
            scanned++;

            TagSystem ts = null;
            try { ts = it.modifiedState; } catch { return; }
            if (ts == null) return;

            if (action == "battery")
            {
                // 上限与当前值两个标签都必须已存在；缺任何一个就整件跳过（绝不新建）
                var maxT = GetExistingTag(ts, BatteryMaxTag);
                var curT = GetExistingTag(ts, BatteryCurTag);
                if (maxT == null || curT == null) return;

                eligible++;                                          // 这件确实是电池
                int max = SafeInt(() => maxT.valueInt);
                if (SafeInt(() => curT.valueInt) == max) return;      // 已经是满的

                try { curT.valueInt = max; } catch { return; }
                changed++;
                tags++;
                return;
            }

            if (action == "water")
            {
                bool counted = false;

                // 严格模式：7 个污染标记必须全在，缺一个就整件跳过
                if (strictWater)
                {
                    for (int k = 0; k < WaterPartTags.Length; k++)
                    {
                        if (GetExistingTag(ts, WaterPartTags[k]) == null) return;
                    }
                    eligible++;
                    counted = true;
                }

                int n = 0, present = 0;
                for (int k = 0; k < WaterPartTags.Length; k++)
                {
                    var st = GetExistingTag(ts, WaterPartTags[k]);
                    if (st == null) continue;    // 这件没有这个污染项 → 完全不碰它
                    present++;
                    if (SafeInt(() => st.valueInt) == 0) continue;      // 本来就是 0

                    try { st.valueInt = 0; } catch { continue; }
                    n++;
                }

                // 宽松模式：只要带上其中任意一个就算符合条件
                if (!counted && present > 0) eligible++;
                if (n > 0) { changed++; tags += n; }
            }
        });

        Log?.LogInfo($"快速作弊 {action}{(strictWater ? "(严格)" : "")}：扫描 {scanned} 件，符合条件 {eligible} 件，实际改动 {changed} 件，涉及 {tags} 个值");
        return "{\"ok\":true,\"action\":" + JStr(action)
             + ",\"strict\":" + (strictWater ? "true" : "false")
             + ",\"scanned\":" + scanned
             + ",\"eligible\":" + eligible
             + ",\"changed\":" + changed
             + ",\"tags\":" + tags + "}";
    }

    private static void WalkInventory(GameInventory inv, System.Collections.Generic.HashSet<int> seen, int depth, Action<GameItem> act)
    {
        if (inv == null || depth > MaxDepth) return;

        Il2CppSystem.Collections.Generic.List<GameItem> items;
        try { items = inv.childItems; } catch { return; }
        if (items == null) return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            WalkItem(it, seen, depth, act);
        }
    }

    private static void WalkItem(GameItem item, System.Collections.Generic.HashSet<int> seen, int depth, Action<GameItem> act)
    {
        if (item == null) return;

        int uid = Uid(item);
        if (uid != 0 && !seen.Add(uid)) return;
        try { act(item); } catch { }

        if (depth >= MaxDepth) return;

        // 物品的容器藏在 child(GraphNodeWindow，实际实现是 PixelWindow) 下面，
        // 所以必须对 GraphNodeStorage 递归下钻：节点可能是物品、库存，也可能是夹在中间的窗口。
        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = item.children; } catch { return; }
        if (children == null) return;

        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            WalkNode(ch, seen, depth + 1, act, 0);
        }
    }

    /// <summary>
    /// 递归下钻一个 GraphNodeStorage。关键点：既不是物品也不是库存的节点（一般是 PixelWindow
    /// 这类中间窗口）不算一层，继续往下钻——否则物品里的容器会被整棵丢掉。
    /// </summary>
    private static void WalkNode(GraphNodeStorage node, System.Collections.Generic.HashSet<int> seen, int depth, Action<GameItem> act, int hops)
    {
        if (node == null || depth > MaxDepth || hops > 8) return;

        GameItem item = null;
        try { item = node.TryCast<GameItem>(); } catch { }
        if (item != null) { WalkItem(item, seen, depth, act); return; }

        GameInventory inv = null;
        try { inv = node.TryCast<GameInventory>(); } catch { }
        if (inv != null) { WalkInventory(inv, seen, depth, act); return; }

        // 中间节点：不涨 depth，只涨 hops（防环）
        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = node.children; } catch { return; }
        if (children == null) return;

        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            WalkNode(ch, seen, depth, act, hops + 1);
        }
    }

    // ---------------------------------------------------------- 安全取值
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static string SafeEnum(Func<string> f) { try { return f() ?? "?"; } catch { return "?"; } }

    private static string JoinStrings(Il2CppSystem.Collections.Generic.List<string> list)
    {
        if (list == null || list.Count == 0) return "（无）";
        var sb = new System.Text.StringBuilder();
        int max = list.Count > 8 ? 8 : list.Count;
        for (int i = 0; i < max; i++) { sb.Append(list[i]); if (i < max - 1) sb.Append(", "); }
        if (list.Count > max) sb.Append("…");
        return sb.ToString();
    }

    // =============================================== 给外部 UI 的 JSON 接口
    private static int _jsonW;
    private static int _jsonCount;

    private const int MaxJsonItems = 8000;

    /// <summary>按 uniqueId 找物品（含容器内部）。</summary>
    internal static GameItem Find(int uniqueId)
    {
        // 先看游戏右键菜单正指着的那件：它未必在库存树里（可能在别的容器或临时库存），
        // 但只要能拿到它就能直接改，不必依赖遍历结果。
        try
        {
            var ctx = ItemContextHandler.current;
            if (ctx != null)
            {
                var cur = ctx.currentItem;
                if (cur != null && Uid(cur) == uniqueId) return cur;
            }
        }
        catch { }

        GameItem found = null;
        ForEachItem(it =>
        {
            if (found == null && Uid(it) == uniqueId) found = it;
        });
        return found;
    }

    /// <summary>
    /// 把全部物品（含容器内）导成【扁平】JSON 数组，外部 UI 自己用 parentUid 拼树。
    /// 刻意拍平：嵌套数组的逗号分隔极易出错，扁平 + parentUid 更稳也更好查。
    /// 字段名与游戏内 [英文字段名] 一致，UI 侧直接拿来用。
    /// </summary>
    internal static string TreeJson()
    {
        _jsonW = 0;
        _jsonCount = 0;

        var sb = new System.Text.StringBuilder();
        sb.Append("{\"ok\":true,\"items\":[");

        EmitInventory(sb, MainInventory(), "主库存", 0, 0);
        EmitInventory(sb, ContextInventory(), "右键所在库存", 0, 0);

        var emp = Emporium();
        if (emp != null)
        {
            EmitInventory(sb, emp._showcaseElement_k__BackingField, "展示柜", 0, 0);
            EmitInventory(sb, emp._frontInvinvElement_k__BackingField, "前背包", 0, 0);
            EmitInventory(sb, emp._backInvinvElement_k__BackingField, "后背包", 0, 0);
            EmitInventory(sb, emp._hiddenElement_k__BackingField, "隐藏区", 0, 0);
            EmitInventory(sb, emp._bazarLeftinvElement_k__BackingField, "集市左侧", 0, 0);
            EmitInventory(sb, emp._trashInvElement_k__BackingField, "垃圾桶", 0, 0);
            EmitInventory(sb, emp._docInvElement_k__BackingField, "文件袋", 0, 0);
        }

        sb.Append("],\"count\":").Append(_jsonCount).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 按 uniqueId 单取一件物品并序列化（不递归子物品、不依赖库存树遍历）。
    /// 这是外部 UI 的兜底入口：树里列不到的物品照样能看能改。
    /// </summary>
    internal static string SingleItemJson(int uniqueId)
    {
        GameItem item = Find(uniqueId);
        if (item == null)
            return "{\"ok\":false,\"error\":\"currentItem 与库存遍历都没找到 uniqueId=" + uniqueId + "\"}";

        var sb = new System.Text.StringBuilder();
        // 物品对象开头的 { 由 AppendItemFields 负责写，这里不能再写一个，
        // 否则会输出 {{，前端 JSON.parse 会报 "expected property name"。
        sb.Append("{\"ok\":true,\"item\":");

        AppendItemFields(sb, item, Uid(item), 0, GuessPath(item), 0);
        sb.Append(",\"itemTypes\":[");

        try
        {
            var types = item.itemTypes;
            if (types != null)
                for (int i = 0; i < types.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JStr(types[i]));
                }
        }
        catch { }

        sb.Append("],\"features\":[");
        try
        {
            var feats = item.itemFeatures;
            if (feats != null)
            {
                int fw = 0;
                for (int i = 0; i < feats.Count; i++)
                {
                    var f = feats[i];
                    if (f == null) continue;
                    if (fw++ > 0) sb.Append(',');

                    sb.Append("{\"index\":").Append(i)
                      .Append(",\"featureType\":").Append(SafeInt(() => (int)f.featureType))
                      .Append(",\"featureTypeName\":").Append(JStr(SafeEnum(() => f.featureType.ToString())))
                      .Append(",\"valueStage\":").Append(SafeInt(() => (int)f.valueStage))
                      .Append(",\"valueModifier\":").Append(SafeInt(() => f.valueModifier))
                      .Append(",\"preExposeValueModifier\":").Append(SafeInt(() => f.preExposeValueModifier))
                      .Append(",\"publicDisplay\":").Append(JStr(f.publicDisplay))
                      .Append(",\"actualDisplay\":").Append(JStr(f.actualDisplay))
                      .Append(",\"identifier\":").Append(JStr(f.identifier))
                      .Append(",\"category\":").Append(JStr(f.category))
                      .Append(",\"isFeatureExposed\":").Append(SafeBool(() => f.isFeatureExposed) ? "true" : "false")
                      .Append(",\"isFeatureDiscovered\":").Append(SafeBool(() => f.isFeatureDiscovered) ? "true" : "false")
                      .Append(",\"isPublicHidden\":").Append(SafeBool(() => f.isPublicHidden) ? "true" : "false")
                      .Append('}');
                }
            }
        }
        catch { }

        sb.Append(']');   // 关掉 features

        // 耐久 / 电量 / 水量 / 容量这些"物品特有数据"全都存在 TagSystem 的 TagState 里
        AppendTagBlock(sb, "state", SafeState(() => item.state));
        AppendTagBlock(sb, "modifiedState", SafeState(() => item.modifiedState));

        int childCount = 0;
        try { var ch = item.children; if (ch != null) childCount = ch.Count; } catch { }
        sb.Append(",\"childCount\":").Append(childCount).Append("}}");
        return sb.ToString();
    }

    private static void EmitInventory(System.Text.StringBuilder sb, GameInventory inv, string path, int parentUid, int depth)
    {
        if (inv == null || depth > MaxDepth || _jsonCount >= MaxJsonItems) return;

        Il2CppSystem.Collections.Generic.List<GameItem> items;
        try { items = inv.childItems; } catch { return; }
        if (items == null) return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            EmitItem(sb, it, path, parentUid, depth);
        }
    }

    private static void EmitItem(System.Text.StringBuilder sb, GameItem item, string path, int parentUid, int depth)
    {
        if (item == null || _jsonCount >= MaxJsonItems) return;

        if (_jsonW > 0) sb.Append(',');
        _jsonW++;
        _jsonCount++;

        int uid = Uid(item);
        string id = item.identifier ?? "?";

        AppendItemFields(sb, item, uid, parentUid, path, depth);
        sb.Append(",\"itemTypes\":[");

        try
        {
            var types = item.itemTypes;
            if (types != null)
                for (int i = 0; i < types.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JStr(types[i]));
                }
        }
        catch { }

        sb.Append("],\"features\":[");
        try
        {
            var feats = item.itemFeatures;
            if (feats != null)
            {
                int fw = 0;
                for (int i = 0; i < feats.Count; i++)
                {
                    var f = feats[i];
                    if (f == null) continue;
                    if (fw++ > 0) sb.Append(',');

                    sb.Append("{\"index\":").Append(i)
                      .Append(",\"featureType\":").Append(SafeInt(() => (int)f.featureType))
                      .Append(",\"featureTypeName\":").Append(JStr(SafeEnum(() => f.featureType.ToString())))
                      .Append(",\"valueStage\":").Append(SafeInt(() => (int)f.valueStage))
                      .Append(",\"valueModifier\":").Append(SafeInt(() => f.valueModifier))
                      .Append(",\"preExposeValueModifier\":").Append(SafeInt(() => f.preExposeValueModifier))
                      .Append(",\"publicDisplay\":").Append(JStr(f.publicDisplay))
                      .Append(",\"actualDisplay\":").Append(JStr(f.actualDisplay))
                      .Append(",\"identifier\":").Append(JStr(f.identifier))
                      .Append(",\"category\":").Append(JStr(f.category))
                      .Append(",\"isFeatureExposed\":").Append(SafeBool(() => f.isFeatureExposed) ? "true" : "false")
                      .Append(",\"isFeatureDiscovered\":").Append(SafeBool(() => f.isFeatureDiscovered) ? "true" : "false")
                      .Append(",\"isPublicHidden\":").Append(SafeBool(() => f.isPublicHidden) ? "true" : "false")
                      .Append(",\"isExposable\":").Append(SafeBool(() => f.isExposable) ? "true" : "false")
                      .Append(",\"isDisabled\":").Append(SafeBool(() => f.isDisabled) ? "true" : "false")
                      .Append(",\"useCondition\":").Append(SafeBool(() => f.useCondition) ? "true" : "false")
                      .Append(",\"usePreExposeValue\":").Append(SafeBool(() => f.usePreExposeValue) ? "true" : "false")
                      .Append(",\"initiallyShown\":").Append(SafeBool(() => f.initiallyShown) ? "true" : "false")
                      .Append(",\"isFeatureMatch\":").Append(SafeBool(() => f.isFeatureMatch) ? "true" : "false")
                      .Append(",\"removedByTool\":").Append(JStr(f.removedByTool))
                      .Append(",\"customIntValue1\":").Append(SafeInt(() => f.customIntValue1))
                      .Append('}');
                }
            }
        }
        catch { }

        sb.Append(']');   // 关掉 features

        // 耐久 / 电量 / 水量 / 容量这些"物品特有数据"全都存在 TagSystem 的 TagState 里
        AppendTagBlock(sb, "state", SafeState(() => item.state));
        AppendTagBlock(sb, "modifiedState", SafeState(() => item.modifiedState));

        // 内含物品数：不能用 children.Count —— 那里面往往只有一个 PixelWindow 中间节点，
        // 真正的物品挂在它下面，所以必须递归数。（UI 用这个数字显示 📦 标记）
        int childCount = CountInner(item, depth + 1, 0);
        sb.Append(",\"childCount\":").Append(childCount).Append('}');

        // 递归：容器内的物品以本物品为 parentUid 继续拍平输出。
        // 必须无条件下钻——children 里可能是 PixelWindow 这类中间节点，它再往下才是库存。
        EmitChildren(sb, item, path + "/" + id, uid, depth + 1);
    }

    /// <summary>
    /// GameItem 重载。Il2CppInterop 生成的类是普通 class，不会隐式转成 GraphNodeStorage 接口，
    /// 必须 TryCast 一次；有了这个重载，调用处可以直接传 GameItem。
    /// </summary>
    private static int CountInner(GameItem item, int depth, int hops)
    {
        if (item == null) return 0;
        GraphNodeStorage node = null;
        try { node = item.TryCast<GraphNodeStorage>(); } catch { return 0; }
        return node == null ? 0 : CountInner(node, depth, hops);
    }

    /// <summary>数一个节点里（穿透中间窗口节点）真正装着多少件物品，含更深层。</summary>
    private static int CountInner(GraphNodeStorage node, int depth, int hops)
    {
        if (node == null || depth > MaxDepth || hops > 8) return 0;

        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = node.children; } catch { return 0; }
        if (children == null) return 0;

        int n = 0;
        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;

            GameItem sub = null;
            try { sub = ch.TryCast<GameItem>(); } catch { }
            if (sub != null) { n += 1 + CountInner(sub, depth + 1, 0); continue; }

            GameInventory inv = null;
            try { inv = ch.TryCast<GameInventory>(); } catch { }
            if (inv != null) { n += CountInventoryItems(inv, depth + 1); continue; }

            n += CountInner(ch, depth, hops + 1);   // 中间窗口节点：不涨 depth
        }
        return n;
    }

    private static int CountInventoryItems(GameInventory inv, int depth)
    {
        if (inv == null || depth > MaxDepth) return 0;

        Il2CppSystem.Collections.Generic.List<GameItem> items;
        try { items = inv.childItems; } catch { return 0; }
        if (items == null) return 0;

        int n = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            n += 1 + CountInner(it, depth + 1, 0);
        }
        return n;
    }

    private static void EmitChildren(System.Text.StringBuilder sb, GameItem item, string childPath, int parentUid, int depth)
    {
        if (depth > MaxDepth || _jsonCount >= MaxJsonItems) return;

        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = item.children; } catch { return; }
        if (children == null) return;

        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            EmitNode(sb, ch, childPath, parentUid, depth, 0);
        }
    }

    /// <summary>
    /// 递归下钻一个 GraphNodeStorage。既不是物品也不是库存的节点（一般是 PixelWindow
    /// 这类中间窗口——物品的容器就挂在它下面）不算一层，继续往下钻，
    /// 否则"窗口下挂库存"的物品会整棵丢失，这就是树里物品不全的原因。
    /// </summary>
    private static void EmitNode(System.Text.StringBuilder sb, GraphNodeStorage node, string path, int parentUid, int depth, int hops)
    {
        if (node == null || depth > MaxDepth || hops > 8 || _jsonCount >= MaxJsonItems) return;

        GameItem item = null;
        try { item = node.TryCast<GameItem>(); } catch { }
        if (item != null) { EmitItem(sb, item, path, parentUid, depth); return; }

        GameInventory inv = null;
        try { inv = node.TryCast<GameInventory>(); } catch { }
        if (inv != null) { EmitInventory(sb, inv, path, parentUid, depth); return; }

        // 中间节点：不涨 depth，只涨 hops（防环）
        Il2CppSystem.Collections.Generic.List<GraphNodeStorage> children;
        try { children = node.children; } catch { return; }
        if (children == null) return;

        for (int i = 0; i < children.Count; i++)
        {
            var ch = children[i];
            if (ch == null) continue;
            EmitNode(sb, ch, path, parentUid, depth, hops + 1);
        }
    }

    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
    private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    private static TagSystem SafeState(Func<TagSystem> f) { try { return f(); } catch { return null; } }

    /// <summary>
    /// 物品自身全部可导出字段（树列表与单件查询共用一份，避免两边字段不一致）。
    /// 调用后紧跟 itemTypes / features / state 等后续段。
    /// </summary>
    private static void AppendItemFields(System.Text.StringBuilder sb, GameItem item, int uid, int parentUid, string path, int depth)
    {
        sb.Append("{\"uniqueId\":").Append(uid)
          .Append(",\"parentUid\":").Append(parentUid)
          .Append(",\"container\":").Append(JStr(path))
          .Append(",\"depth\":").Append(depth)
          .Append(",\"identifier\":").Append(JStr(item.identifier))
          .Append(",\"name\":").Append(JStr(item.name))
          .Append(",\"unitCount\":").Append(SafeInt(() => item.unitCount))
          .Append(",\"unitValue\":").Append(SafeLong(() => item.unitValue))
          .Append(",\"unitBaseValue\":").Append(SafeLong(() => item.unitBaseValue))
          .Append(",\"lateUnitValue\":").Append(SafeLong(() => item.lateUnitValue))
          .Append(",\"backupUnitValue\":").Append(SafeLong(() => item.backupUnitValue))
          .Append(",\"shortDescription\":").Append(JStr(item.shortDescription))
          .Append(",\"longDescription\":").Append(JStr(item.longDescription))
          .Append(",\"flavorText\":").Append(JStr(item.flavorText))
          .Append(",\"customText\":").Append(JStr(item.customText))
          .Append(",\"spritePath\":").Append(JStr(item.spritePath))
          .Append(",\"forceDisableActivate\":").Append(SafeBool(() => item.forceDisableActivate) ? "true" : "false")
          .Append(",\"forceDisableUse\":").Append(SafeBool(() => item.forceDisableUse) ? "true" : "false")
          .Append(",\"activateDefault\":").Append(SafeBool(() => item.activateDefault) ? "true" : "false")
          .Append(",\"isCombatBackpack\":").Append(SafeBool(() => item.isCombatBackpack) ? "true" : "false")
          .Append(",\"canUseOutsideCombat\":").Append(SafeBool(() => item.canUseOutsideCombat) ? "true" : "false")
          .Append(",\"isDebugMenu\":").Append(SafeBool(() => item.isDebugMenu) ? "true" : "false")
          .Append(",\"useSpriteFromMod\":").Append(SafeBool(() => item.useSpriteFromMod) ? "true" : "false")
          .Append(",\"spriteChanged\":").Append(SafeBool(() => item.spriteChanged) ? "true" : "false")
          .Append(",\"triggerOverwatch\":").Append(SafeBool(() => item.triggerOverwatch) ? "true" : "false");
    }

    /// <summary>
    /// 向游戏自己的本地化表要中文文字。LocHelper 是游戏自带的本地化入口
    /// （GetLocalizedItem(key, args) / GetLocalizedName(key)），查不到时返回空串。
    /// </summary>
    internal static string Localized(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        try
        {
            // GetLocalizedItem 有 Il2CppReferenceArray 和 params object[] 两个重载，
            // 直接传 null 会报 CS0121 二义性，所以显式构造一个空的 Il2CppReferenceArray。
            var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(0);
            var s = LocHelper.GetLocalizedItem(key, args);
            if (!string.IsNullOrEmpty(s) && s != key) return s;
        }
        catch { }

        try
        {
            var s = LocHelper.GetLocalizedName(key);
            if (!string.IsNullOrEmpty(s) && s != key) return s;
        }
        catch { }

        return "";
    }

    /// <summary>
    /// 导出一个 TagSystem（GameItem.state / modifiedState）的全部标签。
    /// 物品的"特有属性"就在这：耐久 durability、电量 currentCharge、容量 totalCapacity、
    /// 水的纯度 realPurity 等，值都在 TagState 的 valueInt / valueFloat / valueBool / valueString 上。
    /// </summary>
    private static void AppendTagBlock(System.Text.StringBuilder sb, string field, TagSystem ts)
    {
        sb.Append(",\"").Append(field).Append("\":[");
        if (ts == null) { sb.Append(']'); return; }

        Il2CppSystem.Collections.Generic.Dictionary<string, TagState> dict = null;
        try { dict = ts.dict; } catch { }
        if (dict == null) { sb.Append(']'); return; }

        int w = 0;
        try
        {
            foreach (var kv in dict)
            {
                var st = kv.Value;
                if (st == null) continue;

                if (w++ > 0) sb.Append(',');
                sb.Append("{\"tag\":").Append(JStr(kv.Key))
                  .Append(",\"identifier\":").Append(JStr(SafeStr(() => st.identifier)))
                  .Append(",\"identifierName\":").Append(JStr(SafeStr(() => st.identifierName)))
                  .Append(",\"localized\":").Append(JStr(Localized(kv.Key)))
                  .Append(",\"localizedName\":").Append(JStr(Localized(SafeStr(() => st.identifierName))))
                  .Append(",\"enabled\":").Append(SafeBool(() => st.valueEnabled) ? "true" : "false")
                  .Append(",\"int\":").Append(SafeInt(() => st.valueInt))
                  .Append(",\"float\":").Append(SafeFloat(() => st.valueFloat).ToString("R"))
                  .Append(",\"long\":").Append(SafeLong(() => st.valueLong))
                  .Append(",\"double\":").Append(SafeStr(() => st.valueDouble.ToString("R")))
                  .Append(",\"bool\":").Append(SafeBool(() => st.valueBool) ? "true" : "false")
                  .Append(",\"string\":").Append(JStr(SafeStr(() => st.valueString)))
                  .Append('}');
            }
        }
        catch { }

        sb.Append(']');
    }

    /// <summary>最小 JSON 字符串转义（不引第三方库）。</summary>
    private static string JStr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";

        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
