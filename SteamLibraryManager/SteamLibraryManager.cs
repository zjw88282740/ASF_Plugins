using System;
using System.Collections;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Plugins.Interfaces;
using AngleSharp.Dom;

namespace SteamLibraryManagerPlugin
{
    [Export(typeof(IPlugin))]
    public sealed class SteamLibraryManagerPlugin : IPlugin, IBotCommand2
    {
        public string Name => "Steam Library Manager";
        public Version Version => new Version("1.6.0");

        public Task OnLoaded()
        {
            ASF.ArchiLogger.LogGenericInfo("多账号库管插件已加载！\n用法 1: !syncignore <BotName> (交叉拉黑)\n用法 2: !exportall <BotName> (导出全局)");
            return Task.CompletedTask;
        }

        public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0)
        {
            if (args.Length == 0) return null;
            string command = args[0].ToUpperInvariant();

            if (command == "SYNCIGNORE")
            {
                if (access < EAccess.Master) return "权限不足。";
                _ = Task.Run(() => ExecuteSyncIgnoreAcrossBots(bot));
                return $"🚀 已启动交叉拉黑任务，正在后台执行，请查看 ASF 控制台日志...";
            }
            else if (command == "EXPORTALL")
            {
                if (access < EAccess.Master) return "权限不足。";
                return await ExportAllGames(bot).ConfigureAwait(false);
            }
            return null;
        }

        // ================= 功能 1：全局汇总导出 =================
        private async Task<string> ExportAllGames(Bot targetBot)
        {
            targetBot.ArchiLogger.LogGenericInfo("正在抓取全局游戏库，准备导出...");
            
            var globalGames = new Dictionary<uint, (string Name, List<string> Owners)>();
            
            // 使用反射无视底层 API 变化，强行提取所有 Bot
            var allBots = GetAllBots();

            foreach (var b in allBots)
            {
                if (!b.IsConnectedAndLoggedOn) continue;
                
                var botGames = await GetOwnedGames(b).ConfigureAwait(false);
                if (botGames != null)
                {
                    targetBot.ArchiLogger.LogGenericInfo($"已抓取 {b.BotName}: {botGames.Count} 款游戏。");
                    foreach (var game in botGames)
                    {
                        if (globalGames.TryGetValue(game.AppID, out var data))
                        {
                            if (!data.Owners.Contains(b.BotName))
                            {
                                data.Owners.Add(b.BotName);
                            }
                        }
                        else
                        {
                            globalGames[game.AppID] = (game.Name ?? "未知名称", new List<string> { b.BotName });
                        }
                    }
                }
            }

            if (globalGames.Count == 0) return "❌ 未能抓取到任何游戏。";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("AppID\t游戏名称\t持有账号\t商店链接");
            
            foreach (var kvp in globalGames.OrderBy(x => x.Key))
            {
                string owners = string.Join(", ", kvp.Value.Owners);
                // 注意：不要用 sb.AppendLine($"...")（插值重载 StringBuilder.AppendLine(ref AppendInterpolatedStringHandler)
                // 在 ASF 裁剪过的运行时里被移除，会抛 MissingMethodException）。改用逐段 Append（这些重载均存在）。
                sb.Append(kvp.Key).Append('\t')
                  .Append(kvp.Value.Name).Append('\t')
                  .Append(owners).Append('\t')
                  .Append("https://store.steampowered.com/app/").Append(kvp.Key)
                  .AppendLine();
            }

            string fileName = $"全局游戏库导出_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            
            try
            {
                await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
                return $"✅ 成功导出 {globalGames.Count} 款去重后的游戏！\n文件已保存至: {filePath}\n请全选内容并粘贴至 Excel。";
            }
            catch (Exception ex)
            {
                targetBot.ArchiLogger.LogGenericException(ex);
                return $"❌ 写入文件失败: {ex.Message}";
            }
        }

        // ================= 功能 2：交叉防重购拉黑 =================
        // 注意：ASF 不会按 bot 名参数把插件命令路由到指定 bot（那个名字只是 args[1]，被忽略），
        // 传进来的 bot 永远是固定的“控制器 bot”。所以这里不依赖它，而是自己枚举所有在线账号，
        // 对每一个都执行拉黑 —— 满足“每个账号都要忽略其他账号拥有的游戏”。
        private async Task ExecuteSyncIgnoreAcrossBots(Bot commandBot)
        {
            ASF.ArchiLogger.LogGenericInfo("正在提取全局游戏库，用于拉黑比对...");

            // 只取在线账号，并缓存每个账号自有的游戏（避免后面重复请求）
            var onlineBots = GetAllBots().Where(b => b.IsConnectedAndLoggedOn).ToList();
            var perBotGames = new Dictionary<string, HashSet<uint>>();
            var allAsfGames = new HashSet<uint>();

            foreach (var b in onlineBots)
            {
                var botGames = await GetOwnedGames(b).ConfigureAwait(false);
                if (botGames == null)
                {
                    // 取自有游戏失败：不加入缓存，后面跳过它，避免误把它自己拥有的游戏也拉黑。
                    b.ArchiLogger.LogGenericWarning($"⚠ {b.BotName} 获取自有游戏失败，本次跳过该账号。");
                    continue;
                }
                var set = new HashSet<uint>(botGames.Select(g => g.AppID));
                perBotGames[b.BotName] = set;
                allAsfGames.UnionWith(set);
            }

            ASF.ArchiLogger.LogGenericInfo($"🌐 全局汇总完毕：{perBotGames.Count} 个账号可处理，共 {allAsfGames.Count} 款不重复游戏。开始逐账号拉黑...");

            // 对每个成功获取自有游戏的账号执行拉黑（顺序执行，避免请求过猛）。
            // 拉黑目标 = 全局有、但该账号没有、且尚未忽略过的游戏。
            foreach (var b in onlineBots)
            {
                if (!perBotGames.TryGetValue(b.BotName, out var myGames)) continue; // 跳过取数失败的账号

                // 先查该账号已经“忽略/不感兴趣”的游戏，本次只处理还没忽略的，减少无谓请求。
                // 包一层 try/catch：查询失败就当作“没有已忽略”，回退为尝试全部，绝不因此中断拉黑。
                HashSet<uint> alreadyIgnored;
                try { alreadyIgnored = await GetIgnoredApps(b).ConfigureAwait(false); }
                catch { alreadyIgnored = new HashSet<uint>(); }

                var toIgnore = new List<uint>();
                int skipped = 0;
                foreach (var id in allAsfGames)
                {
                    if (myGames.Contains(id)) continue;                        // 自己拥有 → 不拉黑
                    if (alreadyIgnored.Contains(id)) { skipped++; continue; }  // 已经忽略 → 跳过
                    toIgnore.Add(id);
                }

                b.ArchiLogger.LogGenericInfo($"🛡️ {b.BotName}: 应拉黑 {toIgnore.Count + skipped} 款，已忽略 {skipped} 款(跳过)，本次实际拉黑 {toIgnore.Count} 款。");
                await ProcessIgnoreForBot(b, toIgnore).ConfigureAwait(false);
            }

            ASF.ArchiLogger.LogGenericInfo("🎉 全部账号拉黑任务已完成！");
        }

        private async Task ProcessIgnoreForBot(Bot bot, List<uint> toIgnoreList)
        {
            if (!bot.IsConnectedAndLoggedOn) return;
            if (toIgnoreList.Count == 0) return;

            int successCount = 0, errorCount = 0;
            const int CONCURRENCY_LIMIT = 5;
            const int BATCH_DELAY_MS = 600;

            Uri postUrl = new Uri("https://store.steampowered.com/recommended/ignorerecommendation/");
            Uri referer = new Uri("https://store.steampowered.com/");

            foreach (var chunk in toIgnoreList.Chunk(CONCURRENCY_LIMIT))
            {
                var tasks = chunk.Select(async appId =>
                {
                    // sessionid 不用自己填：ASF 会按 ESession.Lowercase 从 cookie 注入正确的 sessionid。
                    var data = new Dictionary<string, string>(3)
                    {
                        { "appid", appId.ToString() },
                        { "snr", "1_7_15__13" },
                        { "ignore_reason", "2" }
                    };
                    try
                    {
                        // 关键修复：必须用具名参数 data: 把表单传给 data 形参。
                        // 该方法签名为 (Uri request, IReadOnlyCollection headers, IDictionary data, ...)，
                        // 按位置传 (postUrl, data) 会把表单当成 headers、POST body 为空 → Steam 返回 400 BadRequest。
                        var res = await bot.ArchiWebHandler.UrlPostToHtmlDocumentWithSession(postUrl, data: data, referer: referer).ConfigureAwait(false);
                        if (res != null) return true;
                    }
                    catch { }
                    return false;
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                successCount += results.Count(r => r);
                errorCount += results.Count(r => !r);

                bot.ArchiLogger.LogGenericInfo($"🚀 {bot.BotName} 拉黑进度: 成功 {successCount} | 失败 {errorCount} | 总计 {toIgnoreList.Count}");
                await Task.Delay(BATCH_DELAY_MS).ConfigureAwait(false);
            }

            bot.ArchiLogger.LogGenericInfo($"🎉 {bot.BotName} 拉黑完毕！");
        }

        // 查询该账号已“忽略/不感兴趣”的游戏集合（读 dynamicstore/userdata 的 rgIgnoredApps）。
        // 用 UrlGetToJsonObjectWithSession<JsonElement> 直接拿原始 JSON DOM（无反射、裁剪安全，也不经 AngleSharp）。
        // 任何失败都返回空集合 → syncignore 回退为“尝试全部”，不影响正确性。
        private async Task<HashSet<uint>> GetIgnoredApps(Bot bot)
        {
            var result = new HashSet<uint>();
            try
            {
                Uri url = new Uri("https://store.steampowered.com/dynamicstore/userdata/");
                var response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<JsonElement>(url).ConfigureAwait(false);
                if (response == null) return result;

                JsonElement root = response.Content;
                if (root.ValueKind != JsonValueKind.Object) return result;

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "rgIgnoredApps") continue;
                    var ignored = prop.Value;
                    if (ignored.ValueKind == JsonValueKind.Object)
                    {
                        // 形如 {"12345":0,"67890":2}，键就是 appid
                        foreach (var app in ignored.EnumerateObject())
                        {
                            if (uint.TryParse(app.Name, out var id)) result.Add(id);
                        }
                    }
                    else if (ignored.ValueKind == JsonValueKind.Array)
                    {
                        // 兼容数组形式
                        foreach (var el in ignored.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out var id)) result.Add(id);
                            else if (el.ValueKind == JsonValueKind.String && uint.TryParse(el.GetString(), out var id2)) result.Add(id2);
                        }
                    }
                    break;
                }
            }
            catch { /* 忽略任何异常 → 返回空集合，回退为尝试全部 */ }
            return result;
        }

        // ================= 底层基础方法 =================
        // 使用 ASF 官方公开 API（走 Steam 的 Player.GetOwnedGames）。
        // - 按“所有权/许可证”返回完整列表，包含库内被“隐藏”的游戏；
        // - 查询的是 bot 自己，不受资料隐私影响；
        // - 直接返回 appid->名称，无需抓网页/解析 HTML，也不依赖 AngleSharp。
        private async Task<List<GameItem>?> GetOwnedGames(Bot bot)
        {
            // GetOwnedGames 在 steamID==0 时会抛异常；离线账号直接返回 null。
            if (!bot.IsConnectedAndLoggedOn || bot.SteamID == 0) return null;

            Dictionary<uint, string>? owned = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);
            if (owned == null) return null;

            var list = new List<GameItem>(owned.Count);
            foreach (var kvp in owned)
            {
                list.Add(new GameItem { AppID = kvp.Key, Name = kvp.Value });
            }
            return list;
        }

        // ================= 核心防御钩子 (无视 API 重构，强制提取所有账号) =================
        private static IEnumerable<Bot> GetAllBots()
        {
            var botType = typeof(Bot);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            
            // 扫描所有静态属性
            foreach (var prop in botType.GetProperties(flags))
            {
                if (typeof(IEnumerable).IsAssignableFrom(prop.PropertyType))
                {
                    var val = prop.GetValue(null);
                    if (val != null)
                    {
                        var bots = ExtractBots(val);
                        if (bots.Count > 0) return bots;
                    }
                }
            }
            
            // 扫描所有静态字段 (以防被改成了隐藏的内部字段)
            foreach (var field in botType.GetFields(flags))
            {
                if (typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                {
                    var val = field.GetValue(null);
                    if (val != null)
                    {
                        var bots = ExtractBots(val);
                        if (bots.Count > 0) return bots;
                    }
                }
            }
            return Array.Empty<Bot>();
        }

        private static List<Bot> ExtractBots(object collection)
        {
            var list = new List<Bot>();
            if (collection is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Bot b) list.Add(b);
                    else if (item != null)
                    {
                        var type = item.GetType();
                        // 如果它被存成了 Dictionary 字典结构，提取字典的值
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        {
                            var val = type.GetProperty("Value")?.GetValue(item);
                            if (val is Bot kb) list.Add(kb);
                        }
                    }
                }
            }
            return list;
        }
    }

    public class GameItem
    {
        public uint AppID { get; set; }
        public string? Name { get; set; }
    }
}