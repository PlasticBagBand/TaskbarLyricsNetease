# AGENT.md — 项目完整说明（供后续会话快速接管）

> 读完本文件即可完全了解此项目：做什么、怎么实现、每个数据源细节、踩过的所有坑及解法。
> 最后更新：2026-08（Win11 26200，网易云 8.x，中文系统 + 微信输入法环境）

## 0.5 开发规范（强制，每次改动必须遵守）

1. **每次更新功能/配置/行为，必须同时更新两份文档**：
   - `README.md`（用户向文档：功能特性 / 使用方法 / 热键 / 配置说明 / 工作原理 / 常见问题）
   - `AGENT.md`（本项目说明：实现细节 / 数据源 / 坑与解决 / 验证方法）
2. **新增配置项时**，必须同步 5 处：`Config` 字段默认值 + `Load()` 的 case + `WriteDefaults()` 模板 + README 配置表 + AGENT.md 相关段落。
3. **新踩到坑** → 记入 AGENT.md 的"坑"列表（现象 / 根因 / 解决），避免后续会话重复踩。
4. **删功能时**：源码 + config.ini + README + AGENT.md 全部同步删除相关说明。
5. **改动完成后自查**：README 与 AGENT.md 的描述必须与当前代码实际行为一致（配置项清单、默认值、热键、数据源、功能列表逐项核对）。
6. 文档更新与代码改动在**同一次会话内完成**，不要留到以后。

---
---

## 0. 项目一句话

**在 Windows 任务栏上实时显示网易云音乐正在播放的歌词**（类似 macOS 顶栏歌词），单文件 C#（.NET Framework 4.x / csc.exe 编译，零依赖、离线编译），使用本地数据源优先的策略绕过网易云接口风控。

- 位置：`E:\workspace\deepseek-harness\TaskbarLyricsNetease\`
- 程序：`TaskbarLyricsNetease.exe`（正在用户机器上运行）
- 源码：`src/TaskbarLyricsNetease.cs`（单文件，~1800 行，namespace `TaskbarLyricsNetease`）
- 编译：`build.bat`（离线，引用 `lib/` 下随附的 WinRT winmd）
- 配置：`config.ini`（自动生成 + 托盘开关写回）
- 文档：`README.md`（用户向）

## 1. 文件结构

```
TaskbarLyricsNetease/
├── src/TaskbarLyricsNetease.cs   # 全部源码（单文件）
├── lib/                          # 编译期 WinRT 元数据（NuGet Microsoft.Windows.SDK.Contracts 提取）
│   ├── Windows.WinMD             # 合并 facade（必需，否则 CS0012）
│   ├── Windows.Foundation.FoundationContract.winmd
│   ├── Windows.Foundation.UniversalApiContract.winmd   # 含 GSMTC/Ocr 等全部 API 元数据
│   └── Windows.Media.MediaControlContract.winmd
├── build.bat / run.bat
├── config.ini                    # 运行配置
├── README.md                     # 用户文档
└── AGENT.md                      # 本文件
```

## 2. 源码结构（按类）

| 类 | 职责 |
| --- | --- |
| `Config` | static 配置：加载/保存 config.ini、全部配置项默认值 |
| `Native` | P/Invoke：窗口/置顶/热键/分层窗口/托盘图标销毁/SetParent 等 |
| `Log` | 日志：Console + lyrics.log（`LogToFile` 控制） |
| `Lrc` | LRC 解析（`[mm:ss.xx]`，支持 offset、过滤元信息行）、按进度找行 |
| `NeteaseApi` | HTTP：搜索（兜底）/ 歌词 / 歌曲详情（用 HttpWebRequest + JavaScriptSerializer） |
| `NeteaseDb` | **winsqlite3.dll P/Invoke 只读查询 webdb.dat**：playtime 锚定、最新播放记录、dbTrack 歌曲库索引 |
| `SmtcSource` | SMTC 媒体会话：拿 歌名/歌手/播放状态（Pos 恒 0 拿不到进度） |
| `CacheSource` | 本地缓存：`.uc` 前缀=歌曲id、Temp 歌词缓存（MD5(id)） |
| `LyricsEngine` | 核心状态机：换歌检测、进度估算、歌词加载（generation 机制）、锚定校准、seek 检测、跳行 |
| `OverlayForm` | 任务栏悬浮窗（Solid 渲染）、托盘图标、热键 |
| `Program` | 入口 + `--probe` 自检 |

## 3. 核心数据流

```
轮询(500ms, UI Timer) → LyricsEngine.Poll():
  1. SMTC 取当前 歌名/歌手/状态
  2. 换歌检测：key = "t:歌名|歌手"（SMTC 优先）或 "id:xxx"（无 SMTC 时）
  3. 换歌 → 清歌词 → StartLyricLoad（后台线程）
  4. 进度 = (now - anchorUtc) - pausedTotal + OffsetMs
  5. 取当前歌词行 → DrawNow 渲染

StartLyricLoad（后台线程，generation=++_gen）：
  歌曲 id 解析链（搜索可能被风控，本地优先）：
    ① NeteaseDb.FindTrackIdsByName(SMTC歌名)     ← dbTrack 歌曲库（1103 条，按歌名）
    ② cacheIdHint（.uc 前缀）经 song/detail 歌名校验
    ③ historyTracks 最新记录（歌名校验，写入延迟自动重试 3 次：2s/3.5s）
    ④ NeteaseApi.SearchSong 兜底（接口正常时）
  歌词获取链：
    ① CacheSource.TryGetTempLyric(id)   ← Temp\<MD5(歌曲id)> 网易云自己的歌词缓存
    ② NeteaseApi.FetchLyric(id)         ← /api/song/lyric?id=（不受搜索风控影响）
  generation 检查：myGen != _gen 则丢弃结果（快速切歌时旧线程作废）
  最后 TryAnchorFromDb(id) 校准播放起点
```

## 4. 关键数据源细节（实测验证）

| 源 | 路径/接口 | 说明 |
| --- | --- | --- |
| SMTC | `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` | AUMID=`cloudmusic.exe`；`TryGetMediaPropertiesAsync()` 拿 title/artist；**Pos 恒 0、LastUpdatedTime 固定 08:00:00**（网易云不上报进度） |
| 歌曲 id（缓存） | `%LOCALAPPDATA%\NetEase\CloudMusic\Cache\Cache\<歌曲id>-3999-*.uc` | 文件名前缀=歌曲 id；0 字节是预取需过滤（>150KB 才算真播） |
| 歌词缓存 | `%LOCALAPPDATA%\NetEase\CloudMusic\Temp\<MD5(歌曲id)>` | 网易云客户端缓存的歌词 API 响应 JSON，取 `lrc.lyric` 字段；文件名=MD5(歌曲id字符串) |
| 播放历史 | `%LOCALAPPDATA%\NetEase\CloudMusic\Library\webdb.dat`（SQLite）表 `historyTracks`：`playtime/id/jsonStr` | `playtime`（毫秒）= 歌曲**开始播放的时刻**（用于锚定）；最新一条=当前歌；**短播放（几秒）不写记录、反复同歌不写新记录** |
| 歌曲库 | 同库表 `dbTrack`：`id/jsonStr`（jsonStr 含 `"name":"..."`） | 本地歌曲索引，按歌名匹配拿 id |
| 播放统计 | 同库表 `playingCount`：`playDuration/updateTime` | 仅歌曲结束时累计，**无实时进度** |
| 歌词接口 | `https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1` | 返回正常（搜索被风控但歌词接口 OK）；需 UA + Referer + Cookie `os=pc; appver=8.9.0` |
| 搜索接口 | `https://music.163.com/api/search/get/web?s=...&type=1` | **会被风控**：`result` 变成加密字符串，非 JSON 数组 |

## 5. 显示实现（Solid 模式）

- **窗口**：无边框 `Form`，`TopMost`，`ShowInTaskbar=false`，位置=任务栏 rect 内（`Shell_TrayWnd` + `GetWindowRect`，DPI-aware 物理坐标）
- **点击穿透**：`WndProc` 拦截 `WM_NCHITTEST(0x84)` 返回 `HTTRANSPARENT(-1)`（两种模式通用）
- **保持置顶**：每 500ms `SetWindowPos(Handle, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|NOSIZE|NOACTIVATE)`（对抗 Explorer 点击任务栏时把任务栏提升到置顶层顶部的行为）
- **渲染**：`CreateGraphics()` 直接画（不走 Paint 消息管线）；`Clear(BackdropColor)` + DrawString；前缀（歌名-歌手 | 歌词，`CleanTitle` 去括号）用白色、歌词部分用 `DrawLyric`（KTV 可选）
- **宽度自适应**：`FitSolidWidth` 按文字宽量化 16px 步长调整窗口宽度（max = 任务栏宽-1700，避开 Win11 居中图标和托盘）
- **背景/位置贴合**：`BackdropColor=#1C1C1C`（=任务栏主体 RGB(28,28,28)）；`TrayInsetV=3`（上下内缩，避开任务栏顶部 1px 亮边 RGB(64,64,64)）
- **Layered 模式**（`RenderMode=Layered`）：UpdateLayeredWindow + 32bppPArgb 位图，**该用户环境不可见**（见坑 2），仅保留代码

## 6. 进度估算与锚定

- `_anchorUtc`：换歌时=检测时刻；随后 `TryAnchorFromDb` 用 historyTracks 的 playtime 修正到歌曲真实开始时刻（**只接受 45 秒内**的新鲜 playtime，见坑 7）
- `pos = (now - anchorUtc) - pausedTotalMs + OffsetMs`
- 暂停：SMTC Status=="Paused" 时冻结（记录 `_pauseStarted`，恢复时累加 `_pausedTotalMs`）
- 用户 seek（拖进度条）：无自动来源，`Alt+Shift+↑/↓` 跳句并重锚定（`_anchorUtc = now - (行时间戳 - OffsetMs)`）
- `CheckSeekSignal`：当前歌 `.uc` mtime 4 秒内刷新 → 提示"按 Alt+Shift+↑/↓ 对齐"（不可靠，同歌 seek 不一定刷新缓存）

## 7. 热键 / 托盘

- 热键（RegisterHotKey + WM_HOTKEY）：**Alt+Shift** + `←`/`→`（OffsetMs ±500ms）、`↑`/`↓`（跳句）
- 托盘（NotifyIcon，代码画 ♪ 图标）：右键菜单 =
  - **设置**(打开 config.ini) + **字体大小**(±2px，`SetFontSize`：重建 `_font` + `Config.Save()` + `RefreshDisplay()`，范围 10~40) +
  - 6 个复选开关（`CheckOnClick`，点击→改 Config→`Config.Save()` 写回→`RefreshDisplay()`）+
  - 显示/隐藏歌词 + 退出
- `Config.Save()` 会写回：`FontSize` + 6 个开关项（SetOrAdd 按 key 更新/追加，保留注释）
- **宽度策略（重要）**：歌词条宽度**始终自动**随歌词伸缩（`FitSolidWidth` 按 文字宽+24 量化 16px）；
  最大宽度上限 = `Config.TrayMaxWidth`（0=自动计算 `任务栏宽-900`，>0=固定上限）。
  **曾实现过"固定宽度 TrayWidth"但用户要求回滚**（2026-08，用户想要自动伸缩）；
  **默认上限从 任务栏宽-1700 改为 -900**（长歌词如"世界真奇怪 我穿的吃的 梳个头发 都要向你交代"，
  26px 字号约 24 字 ≈ 620px + 前缀会超 860px 被截断；-900=1660px 足够显示全，代价是超长歌词可能盖住中间图标区）。

## 8. 编译命令（build.bat 内）

```
csc.exe /nologo /target:exe /optimize+ /codepage:65001 /out:TaskbarLyricsNetease.exe ^
  /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll ^
  /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll ^
  /r:lib\Windows.WinMD /r:lib\Windows.Foundation.FoundationContract.winmd ^
  /r:lib\Windows.Foundation.UniversalApiContract.winmd /r:lib\Windows.Media.MediaControlContract.winmd ^
  src\TaskbarLyricsNetease.cs
```
- csc = .NET Framework 编译器 = **C# 5**（见坑 9）
- 运行：`TaskbarLyricsNetease.exe [--console|--probe]`；停止：`taskkill /IM TaskbarLyricsNetease.exe`；**编译前先杀进程**（exe 被占用会 CS0016）

## 9. 验证方法（本环境适用）

- **窗口表面**：小 C# 程序 GetDC(窗口)+BitBlt 统计白色像素（RGB>190 采样），>0 即歌词在渲染
- **z 序**：EnumWindows 顺序中 overlay 必须在 Shell_TrayWnd 之前（被点击后靠 SetWindowPos 拉回）
- **引擎**：`lyrics.log`（需 `LogToFile=true`）看 `[换歌]/[歌词]/[校准]/[忽略旧播放起点]`
- **API 状态**：直接 Invoke-WebRequest 看原始返回（搜索被风控时 result 是加密串）
- **最终裁判是用户**：本环境截图抓不到自建窗口（坑 2），能确认 UI 的只有用户眼睛

---

# 踩过的坑与解决方案（按时间线）

## 坑 1：WinForms TransparencyKey 窗口不上屏
- **现象**：覆盖任务栏的 topmost 透明窗口（TransparencyKey=Magenta）屏幕上看不到，z 序/可见性/位置全对，PrintWindow(2) 能抓到内容但屏幕无。
- **排查**：PrintWindow(0/1/2) 对比 + GetDC BitBlt 读窗口 GDI 表面。
- **解决**：放弃透明方案，**Solid 模式**（普通不透明窗口 + WM_NCHITTEST 穿透），任何环境必然可见。保留 Layered 代码供其他机器。

## 坑 2：本环境屏幕截图（CopyFromScreen / GetDC(0)）抓不到自建窗口
- **现象**：连"普通红色测试窗口"都从截图中消失；但能抓到 explorer 内容（任务栏）。
- **结论**：虚拟/远程显示环境下屏幕捕获与真实显示分离。**不要用截图验证 UI**；窗口自身 GetDC/PrintWindow 正常，最终以用户确认为准。
- 派生结论：**任务栏歌词条到底显不显示，只能靠用户反馈**；我侧只能验证"窗口存在+表面有内容+z 序正确"。

## 坑 3：点击任务栏后歌词条消失
- **现象**：用户"点任务栏歌词就消失"。
- **根因**：Explorer 点击任务栏时把 `Shell_TrayWnd` 提升到置顶层（topmost band）顶部，盖住我们的 topmost 窗口（z 序实测点击后反转为 tray < overlay）。
- **解决**：每 500ms `SetWindowPos(HWND_TOPMOST...)` 压回（实测点击后 1.2s 内恢复在任务栏之上）。
- **备选（失败）**：`SetParent(overlay, Shell_TrayWnd)` 挂子窗口——**WinForms Form 是 TopLevel=true，被外部 SetParent 后窗口句柄被破坏**（EnumThreadWindows 找不到主窗口，进程活着但窗口没了），放弃。

## 坑 4：配置解析把 `#` 开头的颜色值截断 → 透明画刷 → 歌词看不见
- **现象**：歌词条有深色底但文字全不可见（用户反馈"没反应"）。
- **根因**：去行内注释 `int hash = v.IndexOf('#'); if (hash >= 0) v = v.Substring(0, hash)` 把 `TextColor=#FFFFFF` 截成空串 → `ColorTranslator.FromHtml("")` 返回全透明 → 画刷透明。
- **定位**：日志打印 `Config.TextColor.ToArgb()` = `0x00000000`。
- **解决**：`hash > 0` 才截断（`#` 开头是颜色值不是注释）。**教训：所有 `#RRGGBB` 颜色配置项都要小心"去注释"逻辑**。

## 坑 5：网易云搜索接口被风控
- **现象**：某时刻起歌词全部 `id=? 行数=0`（显示歌名）。
- **根因**：`/api/search/get/web` 返回 `{"result":"<64位以上加密串>"}`（result 变成字符串而非对象），反爬。
- **验证**：手动 Invoke-WebRequest 看原始返回；`/api/song/lyric` 仍正常。
- **解决**：id 解析改**本地优先链**（dbTrack 歌曲库 → historyTracks → .uc 缓存前缀 → 搜索兜底），搜索只在接口恢复时兜底。**新增本地来源时注意先后顺序和歌名校验（SameSong 归一化包含匹配）**。

## 坑 6：historyTracks 写入延迟
- **现象**：换歌瞬间查不到新歌的 historyTrack（客户端延迟几秒才写）。
- **解决**：重试 3 次（间隔 2000/3500ms 递增）。

## 坑 7：反复切歌歌词不从开头（旧 playtime 锚定错位）
- **现象**：反复切歌（尤其切回同一首）后歌词显示中间/末尾行。
- **根因**：`GetTrackStartUtc(id)` 返回该歌**上次播放**的 playtime（客户端反复播放同一首歌不写新记录）→ 锚点错设为几分钟前 → 进度虚高。
- **解决**：只接受 **45 秒内**的 playtime（预加载切歌延迟实测最大 38s，可覆盖）；更久的一律忽略并记日志 `[校准] 忽略旧播放起点`（歌词从头开始）。
- **权衡**：阈值过大会误用旧记录；过小会丢掉预加载校准（切歌后歌词显示中间行）。45s 是实测折中。

## 坑 8：`_loading` 互斥导致快速切歌歌词加载被跳过
- **现象**：快速切歌时新歌一直显示歌名（歌词加载线程卡在旧歌的 Sleep 重试里，`_loading=true` 挡掉新歌）。
- **解决**：**generation（代数）机制**：`StartLyricLoad` 开头 `int myGen = ++_gen`，worker 每次写结果前检查 `myGen != _gen` 则丢弃；不再因 `_loading` 直接 return，总是启动新加载。同时把 `_id` 更新和锚定移进 worker 并同样受 generation 保护。

## 坑 9：C# 5 语法限制（csc.exe 是 .NET Framework 编译器）
- `?.` 空条件运算符 → **不支持**（用三元）
- 表达式内声明 `out R r`（C#7）→ 不支持（先声明变量）
- `async Main` → 不支持（用 `.AsTask().GetAwaiter().GetResult()`）
- 字符串插值 `$"..."` → 不支持（用 `+` 拼接）
- **C# 字符串内嵌套双引号**会直接语法错误（中文引号“”安全）

## 坑 10：JavaScriptSerializer 反序列化数组是 ArrayList 不是 object[]
- **现象**：API 解析"查询失败"。
- **根因**：`(object[])songsObj` 抛 InvalidCastException。
- **解决**：用 `System.Collections.ArrayList` 强转（`songs.Cast<...>()` 也换 foreach）。

## 坑 11：WinRT winmd 编译引用（SMTC）
- **现象**：引用 `C:\Windows\System32\WinMetadata\*.winmd` 报 CS0012（`IAsyncOperation` 等定义在合并的 `Windows` 程序集 255.255.255.255 中）；本机无 Windows SDK。
- **解决**：NuGet 下载 `Microsoft.Windows.SDK.Contracts`（已提取到 `lib/`），引用 **contract 分片 winmd**（FoundationContract / UniversalApiContract / MediaControlContract）+ **`Windows.WinMD` facade** + GAC 的 `System.Runtime.WindowsRuntime.dll`。
- **运行时 API 版本差异**：Win11 26200 用 `TryGetMediaPropertiesAsync()`（同步 `GetTimelineProperties`/`GetPlaybackInfo`）；老系统反射回退 `GetGlobalProperties`。
- **不要重复引用** System32 的同名 winmd（CS0433 类型冲突）。

## 坑 12：热键组合被占用/输入法拦截
- `Ctrl+Alt+←/→`：RegisterHotKey 返回 False（被占用）。
- `Ctrl+Shift+←/→`：注册成功但按了不触发——**中文输入法拦截 Ctrl+Shift**（切换输入法快捷键）。
- **解决**：`Alt+Shift+方向键`（实测全部可注册且可触发）。
- 另：keybd_event 模拟按键在本环境不可靠（验证热键需用户实际按键 + 日志）。

## 坑 13：任务栏顶部亮边与背景色
- 实测任务栏主体 RGB(28,28,28)=`#1C1C1C`，**顶部 1px 是 RGB(64,64,64) 亮边**；歌词条背景若用 #202020(32) 会偏亮被看出色差。
- **解决**：`BackdropColor=#1C1C1C`；`TrayInsetV=3` 上下内缩避开亮边（用户反馈"高度再低一点，任务栏最上面有一条不是黑色"）。

## 坑 14：实时进度源全部不可行（结论记录）
尝试过的所有"实时播放进度"来源，均不可行：
| 来源 | 结果 |
| --- | --- |
| SMTC Pos | 恒 0，seek 后也不变 |
| webdb.dat persistentModel（playingListHandoff） | 不随播放更新 |
| webdb.dat playingCount | 歌曲结束时才累计 |
| localdata | 加密二进制（试过常见 XOR 密钥无果） |
| Statics\index.dat | 只是 HTTP 图片缓存 |
| **OCR 网易云窗口**（Windows.Media.Ocr 识别进度时间） | PrintWindow 抓 Chromium 缺 GPU 控制栏层；CopyFromScreen 被其他窗口遮挡 → 不可行 |
因此进度只能估算（playtime 锚定 + 计时），用户 seek 后手动对齐（Alt+Shift+↑/↓）。

## 坑 15：苹方字体安装
- Windows 无苹方。从 GitHub `ZWolken/PingFang`（专为 Windows 转换的 OTF）下载 **PingFangSC-Regular/Medium/Semibold.otf**（各 ~13MB，raw.githubusercontent 慢，需长 timeout/重试）。
- 用户级安装（免管理员）：放 `%LOCALAPPDATA%\Microsoft\Windows\Fonts\` + 写 `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts` + `AddFontResourceW` 立即加载。
- 族名 = **`苹方-简`**（GDI+ 用这个名字，不是 PingFang SC）；`config.ini` 里 `FontName=苹方-简`。
- 同族多文件重复 AddFontResource 可能返回 0（已加载过），不影响使用。

## 坑 16：config.ini 旧模板不含新增项
- 首运行生成的 config.ini 不含后来新增的配置行（RenderMode/BackdropColor/TrayInsetV/ShowSongTitle 等）→ 程序用代码默认值兜底 ✓ 正常。
- **新增配置项时**：改 `Config` 字段默认 + `Load()` 的 case + `WriteDefaults()` 模板 + README 表；旧 config.ini 不会自动补行（默认值兜底），托盘开关 `Save()` 会写回被切过的项。

## 坑 17：下载大文件/网络慢
- raw.githubusercontent / 大文件下载在用户网络慢，`Invoke-WebRequest -TimeoutSec` 需 300-600s，中断后检查文件大小重下（曾出现 4.8MB/13MB 不完整文件）。

---

## 常见操作速查

- 改代码 → 杀进程 → `build.bat`（或手动 csc）→ 启动 → 让用户确认
- 看引擎状态：config `LogToFile=true` → 重启 → 读 `lyrics.log`
- 自检：`TaskbarLyricsNetease.exe --probe`
- 重置配置：删 `config.ini` 重启（自动重建默认模板）
- 用户机器字体：苹方-简 已装（勿重复安装）
- **用户偏好**：纯白歌词（KTV 关）、苹方 26px、显示歌名-歌手前缀（括号隐藏）、背景贴任务栏

## 未来可扩展方向（未做）

- 开机自启（启动项/注册表）
- 多显示器任务栏支持（目前只主屏 Shell_TrayWnd）
- 图形化设置窗口（替代记事本）
- 翻译歌词（tlyric）
- 界面主题/配色
- 若网易云未来上报 SMTC 进度（Pos 非 0），代码已预留 `smtc.PosMs > 0` 直接使用
