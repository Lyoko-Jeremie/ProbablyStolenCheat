using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;

namespace ProbablyStolenCheat;

/// <summary>
/// 外部 UI ↔ 游戏进程的本地 HTTP 通道（默认 http://127.0.0.1:8787/）。
///
/// 为什么需要它：Unity 的游戏对象只能在主线程访问，而 HTTP 请求在后台线程。
/// 所以所有请求都先排进 _mainQueue，由 CheatBehaviour.Update() 在主线程执行完再回传结果。
///
/// 落盘走游戏自己的存读档入口，避免任何手写序列化的格式风险：
///     SaveManager.Save()  /  SaveManager.Load()
/// </summary>
internal static class SaveServer
{
    internal static ManualLogSource Log;

    private const int Port = 8787;
    private const int MainThreadTimeoutMs = 10000;

    private static System.Net.Sockets.TcpListener _listener;
    private static Thread _acceptThread;
    private static volatile bool _running;
    private static readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();

    // ================================================================ 生命周期
    internal static void Start()
    {
        if (_running) return;
        try
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ps-cheat-http" };
            _acceptThread.Start();
            Log?.LogInfo($"外部 UI 已就绪，浏览器打开 http://127.0.0.1:{Port}/ （界面文件：{Plugin.UiDir}）");
        }
        catch (Exception e)
        {
            Log?.LogError($"启动 HTTP 服务失败（端口 {Port} 可能被占用）：{e.Message}");
        }
    }

    internal static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    /// <summary>由 CheatBehaviour.Update() 每帧调用：把 HTTP 线程排队的操作放到主线程执行。</summary>
    internal static void PumpMainThread()
    {
        int budget = 32;   // 每帧最多处理 32 条，避免掉帧
        while (budget-- > 0 && _mainQueue.TryDequeue(out var act))
        {
            try { act(); }
            catch (Exception e) { Log?.LogWarning("主线程任务失败：" + e.Message); }
        }
    }

    // ================================================================ HTTP 服务
    private static void AcceptLoop()
    {
        while (_running)
        {
            System.Net.Sockets.TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (!_running) return; continue; }

            var c = client;
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
    }

    private static void Handle(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                ns.ReadTimeout = 10000;
                var reader = new System.IO.StreamReader(ns, Encoding.UTF8, false, 4096, true);

                string requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine)) return;

                int contentLength = 0;
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
                }

                string body = "";
                if (contentLength > 0)
                {
                    var buf = new char[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = reader.Read(buf, read, contentLength - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    body = new string(buf, 0, read);
                }

                var bits = requestLine.Split(' ');
                string method = bits.Length > 0 ? bits[0] : "GET";
                string path = bits.Length > 1 ? bits[1] : "/";
                int q = path.IndexOf('?');
                string query = q >= 0 ? path.Substring(q + 1) : "";
                if (q >= 0) path = path.Substring(0, q);

                string payload;
                string contentType = "application/json; charset=utf-8";

                if (path == "/" || path == "/index.html")
                {
                    payload = UiHtml();
                    contentType = "text/html; charset=utf-8";
                }
                else
                {
                    payload = Route(method, path, query, body);
                }

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                string header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(header);
                ns.Write(hb, 0, hb.Length);
                ns.Write(bytes, 0, bytes.Length);
                ns.Flush();
            }
        }
        catch (Exception e)
        {
            Log?.LogWarning("HTTP 请求处理失败：" + e.Message);
        }
    }

    private static string UiHtml()
    {
        try
        {
            string p = System.IO.Path.Combine(Plugin.UiDir, "index.html");
            if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p, Encoding.UTF8);
        }
        catch (Exception e) { Log?.LogWarning("读取 index.html 失败：" + e.Message); }

        return "<!doctype html><meta charset='utf-8'><body style='font-family:monospace;padding:20px'>" +
               "<h3>ps-cheat 外部 UI 缺失</h3><p>请把 <b>index.html</b> 放到：<br>" + Plugin.UiDir + "</p>" +
               "<p>放好后刷新本页即可。</p></body>";
    }

    // ================================================================== 路由
    private static string Route(string method, string path, string query, string body)
    {
        switch (path)
        {
            case "/api/status": return CallMain(StatusJson);
            case "/api/tree": return CallMain(TreeJson);
            case "/api/money": return CallMain(() => ApiSetCash(body));
            case "/api/favor": return CallMain(() => ApiSetFavor(body));
            case "/api/item": return CallMain(() => ApiSetItem(body));
            case "/api/itemfeature": return CallMain(() => ApiSetFeature(body));
            case "/api/itemzero": return CallMain(() => ApiItemAction(body, "zero"));
            case "/api/itemdisc": return CallMain(() => ApiItemAction(body, "disc"));
            case "/api/boost": return CallMain(ApiBoost);
            case "/api/save": return CallMain(ApiSave);
            case "/api/load": return CallMain(ApiLoad);
            case "/api/savefile": return SaveFileInfo();
            case "/api/ping": return "{\"ok\":true,\"pong\":true}";
            case "/api/focus": return FocusJson();
            case "/api/find": return CallMain(() => ApiFind(query));
            case "/api/itemtag": return CallMain(() => ApiSetTag(body));
            case "/api/localize": return CallMain(() => ApiLocalize(query));
            case "/api/quick":
                {
                    // /api/quick?action=battery 充满所有电池；?action=water[&loose=1] 净化所有水。
                    // 只改 modifiedState。water 默认严格：要求 7 个 CURRENT_PART_* 标记全在才处理；
                    // 加 loose=1 才切成宽松（带其中任意一个，就清它含有的那些）。
                    string q = query ?? "";
                    string act = q.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 ? "water" : "battery";
                    bool strict = q.IndexOf("loose=1", StringComparison.OrdinalIgnoreCase) < 0;
                    return CallMain(() => ItemEditor.DoQuick(act, strict));
                }
            default: return Err("未知接口 " + path);
        }
    }

    /// <summary>GET /api/find?uid=N —— 按 uniqueId 单取一件物品，不依赖库存树遍历。</summary>
    private static string ApiFind(string query)
    {
        int uid = 0;
        foreach (var part in (query ?? "").Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.Substring(0, eq).Equals("uid", StringComparison.OrdinalIgnoreCase))
                int.TryParse(part.Substring(eq + 1), out uid);
        }
        if (uid == 0) return Err("缺少查询参数 uid，例如 /api/find?uid=312");
        return ItemEditor.SingleItemJson(uid);
    }

    /// <summary>
    /// POST /api/itemtag —— 改某个 TagSystem 标签的值（耐久 / 电量 / 容量这些就存在标签里）。
    /// 体：{uniqueId, which:"state"|"modifiedState", tag:"durability", int?, float?, long?, double?, bool?, enabled?, string?}
    /// </summary>
    private static string ApiSetTag(string body)
    {
        using var doc = Parse(body);
        if (doc == null) return Err("请求体不是合法 JSON");

        var root = doc.RootElement;
        if (!root.TryGetProperty("uniqueId", out var uidEl) || !uidEl.TryGetInt32(out var uid))
            return Err("请求体缺少 uniqueId");
        if (!root.TryGetProperty("tag", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            return Err("请求体缺少 tag（标签名）");

        string which = "state";
        if (root.TryGetProperty("which", out var wEl) && wEl.ValueKind == JsonValueKind.String)
            which = wEl.GetString();

        var item = ItemEditor.Find(uid);
        if (item == null) return Err("没找到 uniqueId=" + uid + " 的物品");

        TagSystem ts = null;
        try { ts = which == "modifiedState" ? item.modifiedState : item.state; } catch { }
        if (ts == null) return Err(which + " 为 null（该物品没有这个状态集）");

        string tag = tagEl.GetString();
        var st = ts.GetTag(tag);
        if (st == null) return Err("没有名为「" + tag + "」的标签");

        var changed = new StringBuilder();
        try
        {
            if (root.TryGetProperty("int", out var i) && i.TryGetInt32(out var iv))
            { st.valueInt = iv; changed.Append("int=").Append(iv).Append(' '); }

            if (root.TryGetProperty("float", out var f) && f.TryGetSingle(out var fv))
            { st.valueFloat = fv; changed.Append("float=").Append(fv.ToString("R")).Append(' '); }

            if (root.TryGetProperty("long", out var l) && l.TryGetInt64(out var lv))
            { st.valueLong = lv; changed.Append("long=").Append(lv).Append(' '); }

            if (root.TryGetProperty("double", out var d) && d.TryGetDouble(out var dv))
            { st.valueDouble = dv; changed.Append("double=").Append(dv.ToString("R")).Append(' '); }

            if (root.TryGetProperty("bool", out var b) && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False))
            { st.valueBool = b.GetBoolean(); changed.Append("bool=").Append(b.GetBoolean()).Append(' '); }

            if (root.TryGetProperty("enabled", out var en) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
            { st.valueEnabled = en.GetBoolean(); changed.Append("enabled=").Append(en.GetBoolean()).Append(' '); }

            if (root.TryGetProperty("string", out var s) && s.ValueKind == JsonValueKind.String)
            { st.valueString = s.GetString(); changed.Append("string "); }
        }
        catch (Exception e) { return Err("写入标签失败：" + e.Message); }

        Log?.LogInfo($"外部 UI：{item.identifier} 的 {which}.{tag} → {changed}");
        return "{\"ok\":true,\"changed\":" + JsonStr(changed.ToString().Trim()) + "}";
    }

    /// <summary>GET /api/localize?key=xxx —— 用游戏自己的本地化表把 key 翻成中文。</summary>
    private static string ApiLocalize(string query)
    {
        string key = "";
        foreach (var part in (query ?? "").Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.Substring(0, eq).Equals("key", StringComparison.OrdinalIgnoreCase))
                key = part.Substring(eq + 1);
        }
        if (key.Length == 0) return Err("缺少查询参数 key");

        string text = ItemEditor.Localized(key);
        return "{\"ok\":true,\"key\":" + JsonStr(key) + ",\"text\":" + JsonStr(text) + "}";
    }

    // ---------------------------------------------------- 右键物品 → 外部 UI
    // 游戏内 F10 / 面板按钮写入，外部 UI 轮询取走后清零（0 = 没有待打开的物品）。
    private static int _focusUid;

    /// <summary>游戏内 F10 / 面板按钮调用：把某件物品推给外部 UI 打开。</summary>
    internal static void FocusItem(int uniqueId)
    {
        _focusUid = uniqueId;
    }

    /// <summary>外部 UI 轮询此接口取走焦点物品，取后即清空（一次性消费）。</summary>
    private static string FocusJson()
    {
        int uid = _focusUid;
        _focusUid = 0;
        return "{\"ok\":true,\"uniqueId\":" + uid + "}";
    }

    /// <summary>把请求转到 Unity 主线程执行并等结果。</summary>
    private static string CallMain(Func<string> f)
    {
        string result = null;
        using (var done = new ManualResetEventSlim(false))
        {
            _mainQueue.Enqueue(() =>
            {
                try { result = f(); }
                catch (Exception e) { result = Err(e.Message); }
                finally { done.Set(); }
            });

            if (!done.Wait(MainThreadTimeoutMs))
                return Err("主线程超时（游戏卡住，或还没进入商店场景）");
        }
        return result ?? Err("没有返回结果");
    }

    // ============================================================== 各接口实现
    private static string StatusJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true");

        var ps = Store();
        if (ps == null)
        {
            sb.Append(",\"inGame\":false");
        }
        else
        {
            sb.Append(",\"inGame\":true");
            sb.Append(",\"cash\":").Append(ps.playerCash);
            sb.Append(",\"wildFavor\":").Append(ps.wildFavor);
            sb.Append(",\"slot\":").Append(ps.saveSlotId);
            sb.Append(",\"devMode\":").Append(ps.devMode ? "true" : "false");
            sb.Append(",\"isProduction\":").Append(ps.isProduction ? "true" : "false");
        }

        sb.Append(",\"hasSave\":").Append(SafeHasSave() ? "true" : "false");
        sb.Append(",\"uiDir\":").Append(JsonStr(Plugin.UiDir));
        sb.Append("}");
        return sb.ToString();
    }

    private static string TreeJson()
    {
        return ItemEditor.TreeJson();
    }

    private static string ApiSetCash(string body)
    {
        var ps = Store();
        if (ps == null) return Err("还没进游戏（PlayerStore 未创建）");

        if (!TryReadLong(body, "value", out var v)) return Err("请求体缺少整数 value");
        if (v < 0 || v > int.MaxValue) return Err("金额超出 int 范围（0 ~ " + int.MaxValue + "）");

        ps.playerCash = (int)v;
        Log?.LogInfo($"外部 UI：playerCash = {v}");
        return "{\"ok\":true,\"cash\":" + ps.playerCash + "}";
    }

    private static string ApiSetFavor(string body)
    {
        var ps = Store();
        if (ps == null) return Err("还没进游戏（PlayerStore 未创建）");

        if (!TryReadLong(body, "value", out var v)) return Err("请求体缺少整数 value");
        if (v < 0 || v > int.MaxValue) return Err("好感度过大");

        ps.wildFavor = (int)v;
        Log?.LogInfo($"外部 UI：wildFavor = {v}");
        return "{\"ok\":true,\"wildFavor\":" + ps.wildFavor + "}";
    }

    private static string ApiSetItem(string body)
    {
        using var doc = Parse(body);
        if (doc == null) return Err("请求体不是合法 JSON");

        var root = doc.RootElement;
        if (!root.TryGetProperty("uniqueId", out var uidEl) || !uidEl.TryGetInt32(out var uid))
            return Err("请求体缺少 uniqueId");

        var item = ItemEditor.Find(uid);
        if (item == null) return Err("没找到 uniqueId=" + uid + " 的物品（可能不在当前场景）");

        var changed = new StringBuilder();
        try
        {
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            { item.name = n.GetString(); changed.Append("name "); }

            if (root.TryGetProperty("unitCount", out var c) && c.TryGetInt32(out var cv))
            { item.unitCount = cv; changed.Append("unitCount=").Append(cv).Append(' '); }

            if (root.TryGetProperty("unitValue", out var v) && v.TryGetInt64(out var vv))
            { item.unitValue = vv; changed.Append("unitValue=").Append(vv).Append(' '); }

            if (root.TryGetProperty("unitBaseValue", out var b) && b.TryGetInt64(out var bv))
            { item.unitBaseValue = bv; changed.Append("unitBaseValue=").Append(bv).Append(' '); }

            if (root.TryGetProperty("lateUnitValue", out var l) && l.TryGetInt64(out var lv))
            { item.lateUnitValue = lv; changed.Append("lateUnitValue=").Append(lv).Append(' '); }

            if (root.TryGetProperty("backupUnitValue", out var k) && k.TryGetInt64(out var kv))
            { item.backupUnitValue = kv; changed.Append("backupUnitValue=").Append(kv).Append(' '); }

            if (root.TryGetProperty("shortDescription", out var sd) && sd.ValueKind == JsonValueKind.String)
            { item.shortDescription = sd.GetString(); changed.Append("shortDescription "); }

            if (root.TryGetProperty("longDescription", out var ld) && ld.ValueKind == JsonValueKind.String)
            { item.longDescription = ld.GetString(); changed.Append("longDescription "); }

            if (root.TryGetProperty("flavorText", out var ft) && ft.ValueKind == JsonValueKind.String)
            { item.flavorText = ft.GetString(); changed.Append("flavorText "); }

            if (root.TryGetProperty("customText", out var ct) && ct.ValueKind == JsonValueKind.String)
            { item.customText = ct.GetString(); changed.Append("customText "); }

            // identifier / spritePath 与下面 9 个布尔在 GameItem 上都是只读属性（只有 getter），
            // 必须写 IL2CPP 生成的 backing field（都是 public 的，之前 _invElement_k__BackingField 已验证可行）。
            if (root.TryGetProperty("identifier", out var idp) && idp.ValueKind == JsonValueKind.String)
            { item._identifier_k__BackingField = idp.GetString(); changed.Append("identifier "); }

            if (root.TryGetProperty("spritePath", out var spp) && spp.ValueKind == JsonValueKind.String)
            { item._spritePath_k__BackingField = spp.GetString(); changed.Append("spritePath "); }

            // 布尔开关：只认真正的 true/false（字符串 "false" 不会被当成 true）
            if (BoolField(root, "forceDisableActivate", out var b1)) { item._forceDisableActivate_k__BackingField = b1; changed.Append("forceDisableActivate=").Append(b1).Append(' '); }
            if (BoolField(root, "forceDisableUse", out var b2)) { item._forceDisableUse_k__BackingField = b2; changed.Append("forceDisableUse=").Append(b2).Append(' '); }
            if (BoolField(root, "activateDefault", out var b3)) { item._activateDefault_k__BackingField = b3; changed.Append("activateDefault=").Append(b3).Append(' '); }
            if (BoolField(root, "isCombatBackpack", out var b4)) { item._isCombatBackpack_k__BackingField = b4; changed.Append("isCombatBackpack=").Append(b4).Append(' '); }
            if (BoolField(root, "canUseOutsideCombat", out var b5)) { item._canUseOutsideCombat_k__BackingField = b5; changed.Append("canUseOutsideCombat=").Append(b5).Append(' '); }
            if (BoolField(root, "isDebugMenu", out var b6)) { item._isDebugMenu_k__BackingField = b6; changed.Append("isDebugMenu=").Append(b6).Append(' '); }
            if (BoolField(root, "useSpriteFromMod", out var b7)) { item._useSpriteFromMod_k__BackingField = b7; changed.Append("useSpriteFromMod=").Append(b7).Append(' '); }
            if (BoolField(root, "spriteChanged", out var b8)) { item._spriteChanged_k__BackingField = b8; changed.Append("spriteChanged=").Append(b8).Append(' '); }
            if (BoolField(root, "triggerOverwatch", out var b9)) { item._triggerOverwatch_k__BackingField = b9; changed.Append("triggerOverwatch=").Append(b9).Append(' '); }
        }
        catch (Exception e) { return Err("写入字段失败：" + e.Message); }

        Log?.LogInfo($"外部 UI：改了 {item.identifier}（uniqueId={uid}）→ {changed}");
        return "{\"ok\":true,\"uniqueId\":" + uid + ",\"changed\":" + JsonStr(changed.ToString().Trim()) + "}";
    }

    /// <summary>编辑某件物品的某一条特征：{uniqueId, featureIndex, 若干字段}。</summary>
    private static string ApiSetFeature(string body)
    {
        using var doc = Parse(body);
        if (doc == null) return Err("请求体不是合法 JSON");

        var root = doc.RootElement;
        if (!root.TryGetProperty("uniqueId", out var uidEl) || !uidEl.TryGetInt32(out var uid))
            return Err("请求体缺少 uniqueId");
        if (!root.TryGetProperty("featureIndex", out var idxEl) || !idxEl.TryGetInt32(out var idx))
            return Err("请求体缺少 featureIndex");

        var item = ItemEditor.Find(uid);
        if (item == null) return Err("没找到 uniqueId=" + uid + " 的物品");

        var feats = item.itemFeatures;
        if (feats == null || idx < 0 || idx >= feats.Count) return Err($"特征下标越界（该物品共 {(feats == null ? 0 : feats.Count)} 条）");

        var f = feats[idx];
        if (f == null) return Err("该特征为空");

        var changed = new StringBuilder();
        try
        {
            if (root.TryGetProperty("valueModifier", out var vm) && vm.TryGetInt32(out var vmv))
            { f.valueModifier = vmv; changed.Append("valueModifier=").Append(vmv).Append(' '); }

            if (root.TryGetProperty("preExposeValueModifier", out var pv) && pv.TryGetInt32(out var pvv))
            { f.preExposeValueModifier = pvv; changed.Append("preExposeValueModifier=").Append(pvv).Append(' '); }

            if (root.TryGetProperty("customIntValue1", out var ci) && ci.TryGetInt32(out var civ))
            { f.customIntValue1 = civ; changed.Append("customIntValue1=").Append(civ).Append(' '); }

            if (root.TryGetProperty("publicDisplay", out var pd) && pd.ValueKind == JsonValueKind.String)
            { f.publicDisplay = pd.GetString(); changed.Append("publicDisplay "); }

            if (root.TryGetProperty("actualDisplay", out var ad) && ad.ValueKind == JsonValueKind.String)
            { f.actualDisplay = ad.GetString(); changed.Append("actualDisplay "); }

            if (root.TryGetProperty("removedByTool", out var rt) && rt.ValueKind == JsonValueKind.String)
            { f.removedByTool = rt.GetString(); changed.Append("removedByTool "); }

            if (root.TryGetProperty("isFeatureExposed", out var ex) && (ex.ValueKind == JsonValueKind.True || ex.ValueKind == JsonValueKind.False))
            { f.isFeatureExposed = ex.GetBoolean(); changed.Append("isFeatureExposed=").Append(ex.GetBoolean()).Append(' '); }

            if (root.TryGetProperty("isFeatureDiscovered", out var dc) && (dc.ValueKind == JsonValueKind.True || dc.ValueKind == JsonValueKind.False))
            { f.isFeatureDiscovered = dc.GetBoolean(); changed.Append("isFeatureDiscovered=").Append(dc.GetBoolean()).Append(' '); }

            if (root.TryGetProperty("isPublicHidden", out var ph) && (ph.ValueKind == JsonValueKind.True || ph.ValueKind == JsonValueKind.False))
            { f.isPublicHidden = ph.GetBoolean(); changed.Append("isPublicHidden=").Append(ph.GetBoolean()).Append(' '); }

            if (root.TryGetProperty("valueStage", out var vs) && vs.TryGetInt32(out var vsv))
            { f.valueStage = (ItemFeature.ValueStage)vsv; changed.Append("valueStage=").Append(vsv).Append(' '); }

            if (root.TryGetProperty("featureType", out var ftp) && ftp.TryGetInt32(out var ftv))
            { f.featureType = (ItemFeature.FeatureType)ftv; changed.Append("featureType=").Append(ftv).Append(' '); }

            if (root.TryGetProperty("category", out var cg) && cg.ValueKind == JsonValueKind.String)
            { f.category = cg.GetString(); changed.Append("category "); }

            if (BoolField(root, "isExposable", out var e1)) { f.isExposable = e1; changed.Append("isExposable=").Append(e1).Append(' '); }
            if (BoolField(root, "isDisabled", out var e2)) { f.isDisabled = e2; changed.Append("isDisabled=").Append(e2).Append(' '); }
            if (BoolField(root, "useCondition", out var e3)) { f.useCondition = e3; changed.Append("useCondition=").Append(e3).Append(' '); }
            if (BoolField(root, "usePreExposeValue", out var e4)) { f.usePreExposeValue = e4; changed.Append("usePreExposeValue=").Append(e4).Append(' '); }
            if (BoolField(root, "initiallyShown", out var e5)) { f.initiallyShown = e5; changed.Append("initiallyShown=").Append(e5).Append(' '); }
            if (BoolField(root, "isFeatureMatch", out var e6)) { f.isFeatureMatch = e6; changed.Append("isFeatureMatch=").Append(e6).Append(' '); }
        }
        catch (Exception e) { return Err("写入特征字段失败：" + e.Message); }

        Log?.LogInfo($"外部 UI：{item.identifier} 特征[{idx}] → {changed}");
        return "{\"ok\":true,\"changed\":" + JsonStr(changed.ToString().Trim()) + "}";
    }

    private static string ApiItemAction(string body, string kind)
    {
        using var doc = Parse(body);
        if (doc == null) return Err("请求体不是合法 JSON");
        if (!doc.RootElement.TryGetProperty("uniqueId", out var uidEl) || !uidEl.TryGetInt32(out var uid))
            return Err("请求体缺少 uniqueId");

        var item = ItemEditor.Find(uid);
        if (item == null) return Err("没找到 uniqueId=" + uid + " 的物品");

        Il2CppSystem.Collections.Generic.List<ItemFeature> feats = null;
        try { feats = item.itemFeatures; } catch { }
        if (feats == null) return Err("该物品没有特征");

        int n = 0;
        for (int i = 0; i < feats.Count; i++)
        {
            var f = feats[i];
            if (f == null) continue;
            if (kind == "zero")
            {
                f.valueModifier = 0;
                f.preExposeValueModifier = 0;
            }
            else
            {
                f.isFeatureDiscovered = true;
                f.isFeatureExposed = true;
                f.isPublicHidden = false;
            }
            n++;
        }
        Log?.LogInfo($"外部 UI：{item.identifier} 的 {n} 条特征执行 {kind}");
        return "{\"ok\":true,\"features\":" + n + "}";
    }

    private static string ApiBoost()
    {
        ItemEditor.BoostAllItems();
        return "{\"ok\":true}";
    }

    private static string ApiSave()
    {
        try
        {
            SaveManager.Save();
            Log?.LogInfo("外部 UI：已调用 SaveManager.Save() 落盘");
            return "{\"ok\":true,\"message\":\"已调用游戏自己的存档写入\"}";
        }
        catch (Exception e) { return Err("SaveManager.Save() 失败：" + e.Message); }
    }

    private static string ApiLoad()
    {
        try
        {
            SaveManager.Load();
            Log?.LogInfo("外部 UI：已调用 SaveManager.Load() 重新读档");
            return "{\"ok\":true,\"message\":\"已调用游戏自己的存档读取\"}";
        }
        catch (Exception e) { return Err("SaveManager.Load() 失败：" + e.Message); }
    }

    /// <summary>只报存档文件的时间/大小，绝不触碰内容。</summary>
    private static string SaveFileInfo()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Questing Goose Studio", "Probably Stolen");

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"dir\":").Append(JsonStr(dir)).Append(",\"files\":[");

            bool first = true;
            foreach (var name in new[] { "save_0.es3", "SaveFile.es3", "saves_index.es3", "steam_autocloud.vdf" })
            {
                var path = System.IO.Path.Combine(dir, name);
                var fi = new System.IO.FileInfo(path);
                string state = fi.Exists ? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : "（不存在）";
                long len = fi.Exists ? fi.Length : 0;

                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(JsonStr(name))
                  .Append(",\"exists\":").Append(fi.Exists ? "true" : "false")
                  .Append(",\"length\":").Append(len)
                  .Append(",\"modified\":").Append(JsonStr(state)).Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ================================================================== 小工具
    private static PlayerStore Store()
    {
        try
        {
            if (!PlayerStore.IsInstanceExist()) return null;
            return PlayerStore.Instance;
        }
        catch { return null; }
    }

    private static bool SafeHasSave()
    {
        try { return SaveManager.HasSave(); } catch { return false; }
    }

    /// <summary>从 JSON 取一个严格布尔字段（只认 true/false，其它值一律忽略）。</summary>
    private static bool BoolField(JsonElement root, string key, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(key, out var el)) return false;
        if (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False) return false;
        value = el.GetBoolean();
        return true;
    }

    private static JsonDocument Parse(string body)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch { return null; }
    }

    private static bool TryReadLong(string body, string key, out long value)
    {
        value = 0;
        using var doc = Parse(body);
        if (doc == null) return false;
        return doc.RootElement.TryGetProperty(key, out var el) && el.TryGetInt64(out value);
    }

    private static string JsonStr(string s)
    {
        return JsonSerializer.Serialize(s ?? "");
    }

    private static string Err(string msg)
    {
        Log?.LogWarning("外部 UI 请求出错：" + msg);
        return "{\"ok\":false,\"error\":" + JsonStr(msg) + "}";
    }
}
