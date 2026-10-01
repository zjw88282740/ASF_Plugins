# Steam Library Manager

一个 [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) 插件，用于**多 Steam 账号**的库管理：

- **`!exportall`** —— 汇总导出所有账号的游戏库（去重、标注每款游戏的持有账号，支持同一游戏被多个账号拥有），输出为可粘贴到 Excel 的 TSV 文件。
- **`!syncignore`** —— 交叉防重复购买：让**每个账号**把「其他账号拥有、但自己没有」的游戏在商店里标记为**「不感兴趣 / 忽略」**（`ignore_reason=2`）。执行时会按各账号的实际商店区域批量查询上架状态，只提交当前区域可见的游戏，避免反复请求必定失败的条目。

> 目标场景：你有多个 Steam 账号，希望一处总览全部游戏归属，并让各账号自动忽略掉「全家桶里已有」的游戏。

## 命令

在 ASF 控制台 / IPC (`/Api/Command`) / Steam 聊天中执行：

| 命令 | 作用 |
|------|------|
| `!exportall` | 导出全局去重游戏库到 `全局游戏库导出_<时间>.txt`（AppID、名称、持有账号、商店链接） |
| `!syncignore` | 对**所有在线账号**执行交叉忽略 |

> 注意：ASF **不会**按 bot 名参数把插件命令路由到指定 bot（那个名字只是普通参数，会被忽略）。因此这两个命令都**自行枚举全部在线账号**，一条命令即处理所有号，`!syncignore` / `!syncignore ASF` / `!syncignore 任意号名` 效果相同。

## 依赖与前置条件

- 你的每个账号都已作为 ASF bot **配置并登录**。
- 插件通过 ASF 官方公开 API `ArchiHandler.GetOwnedGames` 获取游戏列表（按所有权返回，**包含库内隐藏的游戏**，不受资料隐私影响）。
- 「忽略」用的是商店前端接口 `store.steampowered.com/recommended/ignorerecommendation/`（**无官方开放 API**，必须借助 ASF 已登录的网页会话）。

### 只登录、不挂卡

若只想把 ASF 当作多账号登录/会话载体、**不挂卡**，在每个 bot 配置里加：

```json
"FarmingPreferences": 1
```

（`1` = `FarmingPausedByDefault`，起动即暂停挂卡。ASF 6.x 已移除旧的 `"Paused"` 属性。）

## 构建

需要 **.NET 10 SDK**，以及 ASF 自带的引用 DLL（`ArchiSteamFarm.dll`、`System.Composition.AttributedModel.dll`、`AngleSharp.dll`、`SteamKit2.dll`、`protobuf-net.dll`、`protobuf-net.Core.dll`）。它们的所在目录由 csproj 里的 `ASFReferenceDir` 属性指定：

- **本地**：默认指向 `C:\Users\Ayrc\Downloads\ASF-generic`。改成你自己的 ASF-generic 解压目录（编辑 csproj），或直接传参：

  ```bash
  dotnet build SteamLibraryManager/SteamLibraryManager.csproj -c Release \
    -p:ASFReferenceDir=/path/to/ASF-generic
  ```

- **CI（GitHub Actions）**：`.github/workflows/build.yml` 会按 `ASF_VERSION` 自动下载对应的 `ASF-generic.zip`，定位引用目录后传入构建，并把产物 `SteamLibraryManager.dll` 作为 artifact 上传。升级 ASF 时只需改工作流里的 `ASF_VERSION`。

> 这些引用都是 `Private=false`，只用于编译、不会打进插件输出（运行时由 ASF 提供）。

## 部署

把生成的 `SteamLibraryManager/bin/Release/net10.0/SteamLibraryManager.dll` 复制到 ASF 的 `plugins/` 目录，然后重启 ASF。

## 设计要点 / 踩过的坑

- **运行时被 IL 裁剪 (trimmed)**：目标 ASF（`ArchiSteamFarmPlus` 的自包含 win-x64）运行时移除了 ASF 自身未使用的部分 BCL 便捷方法。本插件**刻意规避**了会抛 `MissingMethodException` 的写法：
  - `StringBuilder.AppendLine($"...")`（插值重载被裁）→ 改用分段 `Append`
  - 静态 `Regex.Match(...)`（只剩实例方法）→ 改用官方 API / 手动解析
  - `JsonElement.TryGetProperty`（被裁）→ 改用 `EnumerateObject()`
- **`syncignore` 优化**：先读 `store.steampowered.com/dynamicstore/userdata/` 的 `rgIgnoredApps`，跳过已忽略过的游戏，只 POST 还没忽略的，大幅减少请求量。
- **区域上架预检**：每次执行都读取账号的实际 Steam 商店区域，并通过 `StoreBrowse.GetItems` 每批查询 100 个 AppID。明确为「区域限制」或「不可见」的条目会直接跳过，不发送忽略请求；查询失败或未返回的条目则回退为直接尝试，避免临时接口故障造成漏处理。查询结果不落盘，也不跨次缓存。

## 版本

| 版本 | 说明 |
|------|------|
| 1.7.0 | `syncignore` 按账号商店区域批量预检上架状态，跳过区域限制和不可见条目，不做跨次缓存 |
| 1.6.0 | `syncignore` 读取已忽略列表，跳过已忽略的游戏 |
| 1.5.0 | `syncignore` 改为一条命令处理**全部在线账号** |
| 1.4.0 | 修复忽略请求返回 400（表单需用具名参数 `data:` 传入，避免被当成 headers） |
| 1.3.0 | 改用官方 API `ArchiHandler.GetOwnedGames`（含隐藏游戏，去掉网页抓取） |
| 1.2.0 | 规避裁剪运行时缺失的 BCL 方法 |
| 1.1.0 | 初始版本（网页抓取游戏库） |
