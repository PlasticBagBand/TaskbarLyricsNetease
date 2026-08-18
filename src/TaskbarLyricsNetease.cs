// ============================================================================
// TaskbarLyricsNetease —— 在 Windows 任务栏上显示网易云音乐歌词（仿 macOS 顶栏）
//
// 原理：
//   1. 歌曲身份：优先读网易云客户端本地缓存 Cache\Cache\<歌曲id>-3999-*.uc
//      （文件名前缀即歌曲 id，>300KB 说明真的在播，0 字节是预取，忽略）；
//      同时用 SMTC（Windows 媒体会话）拿 歌名/歌手/播放状态。
//   2. 歌词：网易云官方 API 按歌曲 id 取 LRC（music.163.com/api/song/lyric）；
//      若本地 Temp 目录有对应 MD5(歌曲id) 的歌词缓存则直接用，省一次请求。
//   3. 进度：网易云不上报 SMTC 进度（Pos 恒为 0），因此用"检测到换歌时
//      重置计时器 + OffsetMs 微调"的方式估算进度；暂停时冻结进度。
//   4. 显示：一个无边框、置顶、点击穿透的半透明窗口盖在任务栏上画歌词。
//
// 用法：
//   TaskbarLyricsNetease.exe            正常运行（无控制台窗口）
//   TaskbarLyricsNetease.exe --console  带控制台运行（看日志）
//   TaskbarLyricsNetease.exe --probe    自检模式：打印检测到的信息后退出
//   配置文件 config.ini 首次运行自动生成，可调字号/颜色/位置/进度偏移等。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Windows.Media.Control;

namespace TaskbarLyricsNetease
{
    // ------------------------------------------------------------------ 配置
    public static class Config
    {
        public static string FontName = "Microsoft YaHei UI";
        public static float FontSize = 22f;
        public static string TextColorHex = "#FFFFFF";
        public static string ShadowColorHex = "#000000";
        public static string DimColorHex = "#B0B0B0";          // KTV 未唱到的部分/暂停时的颜色
        public static string Align = "Left";                   // Left / Center
        public static int PaddingX = 150;                      // Left 模式时距任务栏左边缘（避开开始按钮/图标）
        public static int OffsetMs = 0;                        // 歌词进度偏移（毫秒，歌词快了填负值，慢了填正值）
        public static int PollMs = 500;                        // 轮询间隔
        public static bool KtvMode = false;                // 逐字高亮（默认关闭=纯白文字）
        public static bool FilterMetaLines = true;         // 过滤 作词/作曲/编曲 等元信息行
        public static bool DimWhenPaused = false;          // 暂停时变暗（默认关闭）
        public static int BackdropAlpha = 90;                // 歌词半透明黑底透明度 0~255，0 关闭
        public static string RenderMode = "Solid";           // Solid=不透明实心条(最稳) / Layered=分层透明
        public static string BackdropColorHex = "#1C1C1C";   // Solid 模式底条颜色（匹配 Win11 深色任务栏 28,28,28）
        public static int TrayInsetV = 1;                    // 歌词条上下各内缩像素（贴合任务栏可视区）
        public static int TrayMaxWidth = 0;                  // 自动模式下歌词条最大宽度上限px；0=自动(任务栏宽-900)
        public static bool ShowIdleText = false;               // 没在播放时是否显示提示文字
        public static string IdleText = "等待网易云音乐播放…";
        public static bool ShowSongTitle = true;               // 歌词前显示"歌名-歌手"前缀
        public static bool LogToFile = true;
        public static bool AutoWriteConfig = true;

        public static string BaseDir;                          // exe 所在目录
        public static string ConfigPath;

        public static void Init()
        {
            BaseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            ConfigPath = Path.Combine(BaseDir, "config.ini");
            if (File.Exists(ConfigPath)) Load();
            else if (AutoWriteConfig) WriteDefaults();
        }

        static void Load()
        {
            try
            {
                foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int hash = v.IndexOf('#');
                    if (hash > 0) v = v.Substring(0, hash).Trim();   // 去掉行内注释（# 开头的颜色值保留）
                    switch (k.ToLowerInvariant())
                    {
                        case "fontname": FontName = v; break;
                        case "fontsize": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out FontSize); break;
                        case "textcolor": TextColorHex = v; break;
                        case "shadowcolor": ShadowColorHex = v; break;
                        case "dimcolor": DimColorHex = v; break;
                        case "align": Align = v; break;
                        case "paddingx": int.TryParse(v, out PaddingX); break;
                        case "offsetms": int.TryParse(v, out OffsetMs); break;
                        case "pollms": int.TryParse(v, out PollMs); if (PollMs < 100) PollMs = 100; break;
                        case "ktvmode": KtvMode = ParseBool(v, KtvMode); break;
                        case "filtermetalines": FilterMetaLines = ParseBool(v, FilterMetaLines); break;
                        case "dimwhenpaused": DimWhenPaused = ParseBool(v, DimWhenPaused); break;
                        case "backdropalpha": int.TryParse(v, out BackdropAlpha); break;
                        case "rendermode": RenderMode = v; break;
                        case "backdropcolor": BackdropColorHex = v; break;
                        case "trayinsetv": int.TryParse(v, out TrayInsetV); if (TrayInsetV < 0) TrayInsetV = 0; break;
                        case "traymaxwidth": int.TryParse(v, out TrayMaxWidth); if (TrayMaxWidth < 0) TrayMaxWidth = 0; break;
                        case "showidletext": ShowIdleText = ParseBool(v, ShowIdleText); break;
                        case "idletext": IdleText = v; break;
                        case "showsongtitle": ShowSongTitle = ParseBool(v, ShowSongTitle); break;
                        case "logtofile": LogToFile = ParseBool(v, LogToFile); break;
                    }
                }
            }
            catch { /* 配置损坏时用默认值 */ }
        }

        static bool ParseBool(string v, bool dflt)
        {
            bool r; return bool.TryParse(v, out r) ? r : dflt;
        }

        /// <summary>把开关类配置写回 config.ini（保留注释和原有行）</summary>
        public static void Save()
        {
            try
            {
                if (!File.Exists(ConfigPath)) { WriteDefaults(); return; }
                var lines = new List<string>(File.ReadAllLines(ConfigPath, Encoding.UTF8));
                SetOrAdd(lines, "FontSize", FontSize.ToString("0", CultureInfo.InvariantCulture));
                SetOrAdd(lines, "ShowSongTitle", BoolStr(ShowSongTitle));
                SetOrAdd(lines, "KtvMode", BoolStr(KtvMode));
                SetOrAdd(lines, "FilterMetaLines", BoolStr(FilterMetaLines));
                SetOrAdd(lines, "DimWhenPaused", BoolStr(DimWhenPaused));
                SetOrAdd(lines, "ShowIdleText", BoolStr(ShowIdleText));
                SetOrAdd(lines, "LogToFile", BoolStr(LogToFile));
                File.WriteAllLines(ConfigPath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        static string BoolStr(bool b) { return b ? "true" : "false"; }

        static void SetOrAdd(List<string> lines, string key, string value)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith(key + "="))
                {
                    lines[i] = key + "=" + value;
                    return;
                }
            }
            lines.Add(key + "=" + value);
        }

        static void WriteDefaults()
        {
            try
            {
                File.WriteAllText(ConfigPath,
                    "# 任务栏歌词 for 网易云音乐 配置\n" +
                    "# 改完保存后重新运行程序生效\n\n" +
                    "FontName=Microsoft YaHei UI\n" +
                    "FontSize=22\n" +
                    "TextColor=#FFFFFF\n" +
                    "ShadowColor=#000000\n" +
                    "DimColor=#B0B0B0\n" +
                    "Align=Left            # Left / Center\n" +
                    "PaddingX=150          # Left 模式距任务栏左边缘像素（避开开始按钮和图标）\n" +
                    "OffsetMs=0            # 歌词进度偏移毫秒：歌词走得快就填负数，慢就填正数\n" +
                    "PollMs=500            # 轮询毫秒（100~5000）\n" +
                    "KtvMode=false         # 逐字高亮（true=唱到变白/未唱灰色，false=整句纯白）\n" +
                    "FilterMetaLines=true  # 过滤 作词/作曲/编曲 等行\n" +
                    "DimWhenPaused=false   # 暂停时变暗\n" +
                    "BackdropAlpha=90      # Layered 模式歌词半透明黑底透明度 0~255，0 为关闭\n" +
                    "RenderMode=Solid      # Solid=不透明实心条(最稳，默认) / Layered=分层透明(部分环境不可见)\n" +
                    "BackdropColor=#1C1C1C # Solid 模式底条颜色（默认匹配任务栏）\n" +
                    "TrayInsetV=1          # 歌词条上下各内缩像素（贴合任务栏，防高出）\n" +
                    "TrayMaxWidth=0        # 歌词条最大宽度px；0=自动(任务栏宽-900)，长歌词也能显示全\n" +
                    "ShowIdleText=false\n" +
                    "IdleText=等待网易云音乐播放…\n" +
                    "ShowSongTitle=true    # 歌词前显示歌名-歌手前缀，false=只显示歌词\n" +
                    "LogToFile=true\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        public static Color TextColor { get { return ColorTranslator.FromHtml(TextColorHex); } }
        public static Color ShadowColor { get { return ColorTranslator.FromHtml(ShadowColorHex); } }
        public static Color DimColor { get { return ColorTranslator.FromHtml(DimColorHex); } }
        public static Color BackdropColor { get { return ColorTranslator.FromHtml(BackdropColorHex); } }
        public static bool IsSolid { get { return !string.Equals(RenderMode, "Layered", StringComparison.OrdinalIgnoreCase); } }
    }

    // ---------------------------------------------------------------- 原生调用
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);

        // ---- UpdateLayeredWindow 渲染相关 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_TOP = new IntPtr(0);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public static void HideConsole()
        {
            IntPtr h = GetConsoleWindow();
            if (h != IntPtr.Zero) ShowWindow(h, 0);
        }

        /// <summary>取主任务栏矩形（水平任务栏），失败返回 null</summary>
        public static Rectangle? GetTaskbarRect()
        {
            IntPtr h = FindWindow("Shell_TrayWnd", null);
            if (h == IntPtr.Zero) return null;
            RECT r;
            if (!GetWindowRect(h, out r)) return null;
            if (r.Right - r.Left < 50 || r.Bottom - r.Top < 20) return null;
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    // ------------------------------------------------------------------- 日志
    public static class Log
    {
        static readonly object Lock = new object();
        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
            try { Console.WriteLine(line); } catch { }
            if (Config.LogToFile)
            {
                try
                {
                    lock (Lock)
                        File.AppendAllText(Path.Combine(Config.BaseDir, "lyrics.log"),
                            line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    // --------------------------------------------------------------- LRC 解析
    public class LrcLine
    {
        public long TimeMs;
        public string Text;
        public LrcLine(long t, string s) { TimeMs = t; Text = s; }
    }

    public static class Lrc
    {
        static readonly Regex TimeTag = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        static readonly Regex MetaLine = new Regex(
            @"^\s*(作词|作曲|编曲|制作人|制作|和声|和音|混音|母带|录音|监制|吉他|贝斯|鼓|键盘|钢琴|弦乐|配唱|OP|SP|出品|发行|版权|企划|统筹|艺人统筹|封面|设计)[:：\s]", RegexOptions.Compiled);

        public static List<LrcLine> Parse(string lrcText, bool filterMeta, long offsetMs)
        {
            var lines = new List<LrcLine>();
            if (string.IsNullOrEmpty(lrcText)) return lines;
            foreach (var raw in lrcText.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var ms = TimeTag.Matches(line);
                if (ms.Count == 0) continue;
                int lastBracket = line.LastIndexOf(']');
                string text = lastBracket >= 0 ? line.Substring(lastBracket + 1).Trim() : line.Trim();
                if (text.Length == 0) continue;
                if (filterMeta && MetaLine.IsMatch(text)) continue;
                foreach (Match m in ms)
                {
                    int min = int.Parse(m.Groups[1].Value);
                    int sec = int.Parse(m.Groups[2].Value);
                    int msPart = m.Groups[3].Success
                        ? int.Parse(m.Groups[3].Value.PadRight(3, '0').Substring(0, 3))
                        : 0;
                    long t = (min * 60L + sec) * 1000L + msPart + offsetMs;
                    if (t < 0) t = 0;
                    lines.Add(new LrcLine(t, text));
                }
            }
            lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return lines;
        }

        /// <summary>根据进度找当前行；返回 (行号, 行内进度0~1)</summary>
        public static int FindLine(List<LrcLine> lines, long posMs, out double progress)
        {
            progress = 0;
            if (lines == null || lines.Count == 0) return -1;
            int idx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TimeMs <= posMs) idx = i;
                else break;
            }
            if (idx < 0) return 0;                      // 还没到第一句
            if (idx >= lines.Count - 1) { progress = 1; return lines.Count - 1; }
            long cur = lines[idx].TimeMs;
            long next = lines[idx + 1].TimeMs;
            if (next > cur)
                progress = Math.Min(1.0, (double)(posMs - cur) / (next - cur));
            return idx;
        }
    }

    // ------------------------------------------------------------ 网易云 API
    public static class NeteaseApi
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = UA;
                req.Referer = "https://music.163.com/";
                req.Headers["Cookie"] = "os=pc; appver=8.9.0";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Timeout = timeoutMs;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception e)
            {
                Log.Write("[API] 请求失败 " + url + " : " + e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        /// <summary>按 歌名+歌手 搜索，返回 (songId, name, artist)，找不到返回 null</summary>
        public static Tuple<long, string, string> SearchSong(string title, string artist)
        {
            try
            {
                string q = string.IsNullOrEmpty(artist) ? title : title + " " + artist;
                string json = HttpGet("https://music.163.com/api/search/get/web?s=" +
                    Uri.EscapeDataString(q) + "&type=1&limit=8&offset=0", 8000);
                if (json == null) return null;
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                object resObj;
                if (!root.TryGetValue("result", out resObj) || resObj == null) return null;
                var res = (Dictionary<string, object>)resObj;
                object songsObj;
                if (!res.TryGetValue("songs", out songsObj)) return null;
                var songs = (System.Collections.ArrayList)songsObj;
                if (songs.Count == 0) return null;

                string tn = Normalize(title);
                foreach (object so in songs)
                {
                    var s = (Dictionary<string, object>)so;
                    string name = GetStr(s, "name");
                    string art = GetArtist(s);
                    if (MatchName(tn, Normalize(name)))
                        return Tuple.Create(GetLong(s, "id"), name, art);
                }
                // 没有精确匹配就取第一个
                var first = (Dictionary<string, object>)songs[0];
                return Tuple.Create(GetLong(first, "id"), GetStr(first, "name"), GetArtist(first));
            }
            catch { return null; }
        }

        /// <summary>按歌曲 id 取歌词，返回 (lrcText, offsetMs)</summary>
        public static Tuple<string, long> FetchLyric(long id)
        {
            try
            {
                string json = HttpGet("https://music.163.com/api/song/lyric?id=" + id + "&lv=1&kv=1&tv=-1", 8000);
                if (json == null) return null;
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                if (!root.ContainsKey("lrc")) return null;
                var lrc = (Dictionary<string, object>)root["lrc"];
                object lyricObj;
                string lyric = lrc.TryGetValue("lyric", out lyricObj) ? Convert.ToString(lyricObj) : "";
                long offset = 0;
                object offObj;
                if (lrc.TryGetValue("offset", out offObj) && offObj != null)
                    long.TryParse(Convert.ToString(offObj), out offset);
                if (string.IsNullOrEmpty(lyric)) return null;
                return Tuple.Create(lyric, offset);
            }
            catch { return null; }
        }

        /// <summary>按歌曲 id 查歌名/歌手（SMTC 不可用时的兜底）</summary>
        public static Tuple<string, string> FetchSongDetail(long id)
        {
            try
            {
                string json = HttpGet("https://music.163.com/api/song/detail?ids=%5B" + id + "%5D", 8000);
                if (json == null) return null;
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                object songsObj;
                if (!root.TryGetValue("songs", out songsObj)) return null;
                var songs = (System.Collections.ArrayList)songsObj;
                if (songs.Count == 0) return null;
                var s = (Dictionary<string, object>)songs[0];
                return Tuple.Create(GetStr(s, "name"), GetArtist(s));
            }
            catch { return null; }
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                long r;
                if (long.TryParse(Convert.ToString(v), out r)) return r;
            }
            return 0;
        }

        static string GetArtist(Dictionary<string, object> song)
        {
            try
            {
                object a;
                if (song.TryGetValue("artists", out a) && a is System.Collections.ArrayList)
                {
                    var arr = (System.Collections.ArrayList)a;
                    if (arr.Count > 0)
                    {
                        var first = (Dictionary<string, object>)arr[0];
                        return GetStr(first, "name");
                    }
                }
            }
            catch { }
            return "";
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        }

        static bool MatchName(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            return a.Contains(b) || b.Contains(a) || a == b;
        }
    }

    // ---------------------------------------------------- 网易云本地数据库
    // 用系统自带 winsqlite3.dll 只读查询 webdb.dat，拿播放历史里
    // "歌曲开始播放的时刻"（playtime），用于精确锚定进度估算。
    public static class NeteaseDb
    {
        [DllImport("winsqlite3.dll")]
        static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);
        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)]
        static extern int sqlite3_prepare16(IntPtr db, string sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")]
        static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")]
        static extern int sqlite3_close(IntPtr db);

        const int SQLITE_OPEN_READONLY = 0x00000001;
        const int SQLITE_ROW = 100;

        static string DbPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetEase", "CloudMusic", "Library", "webdb.dat");
            }
        }

        /// <summary>查某首歌最近一次"开始播放"的 UTC 时刻；失败返回 null</summary>
        public static DateTime? GetTrackStartUtc(long songId)
        {
            try
            {
                string dbPath = DbPath;
                if (!File.Exists(dbPath)) return null;
                IntPtr db;
                // sqlite3_open_v2 接受 UTF-8 文件名（需 \0 结尾）
                byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
                if (sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                try
                {
                    string sql = "SELECT playtime FROM historyTracks WHERE id='" + songId +
                                 "' ORDER BY playtime DESC LIMIT 1";
                    IntPtr stmt;
                    if (sqlite3_prepare16(db, sql, -1, out stmt, IntPtr.Zero) != 0) return null;
                    DateTime? result = null;
                    try
                    {
                        if (sqlite3_step(stmt) == SQLITE_ROW)
                        {
                            IntPtr p = sqlite3_column_text16(stmt, 0);
                            if (p != IntPtr.Zero)
                            {
                                long ms;
                                if (long.TryParse(Marshal.PtrToStringUni(p), out ms))
                                    result = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                            }
                        }
                    }
                    finally { sqlite3_finalize(stmt); }
                    return result;
                }
                finally { sqlite3_close(db); }
            }
            catch { return null; }
        }

        /// <summary>取播放历史里最新一条记录（即当前正在播放的歌）：返回 (歌曲id, 歌名)</summary>
        public static Tuple<long, string> GetLatestTrack()
        {
            try
            {
                string dbPath = DbPath;
                if (!File.Exists(dbPath)) return null;
                IntPtr db;
                byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
                if (sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                try
                {
                    string sql = "SELECT id, jsonStr FROM historyTracks ORDER BY playtime DESC LIMIT 1";
                    IntPtr stmt;
                    if (sqlite3_prepare16(db, sql, -1, out stmt, IntPtr.Zero) != 0) return null;
                    Tuple<long, string> result = null;
                    try
                    {
                        if (sqlite3_step(stmt) == SQLITE_ROW)
                        {
                            long id = 0;
                            IntPtr p0 = sqlite3_column_text16(stmt, 0);
                            if (p0 != IntPtr.Zero)
                                long.TryParse(Marshal.PtrToStringUni(p0), out id);
                            IntPtr p1 = sqlite3_column_text16(stmt, 1);
                            string json = p1 == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p1);
                            if (id > 0) result = Tuple.Create(id, ExtractName(json));
                        }
                    }
                    finally { sqlite3_finalize(stmt); }
                    return result;
                }
                finally { sqlite3_close(db); }
            }
            catch { return null; }
        }

        static Dictionary<string, List<long>> _trackIndex;   // 归一化歌名 -> id 列表（dbTrack 本地歌曲库）
        static readonly object _trackLock = new object();

        /// <summary>从本地歌曲库 dbTrack 按歌名找 id（本地匹配，不受搜索接口风控影响）</summary>
        public static List<long> FindTrackIdsByName(string title)
        {
            try
            {
                lock (_trackLock)
                {
                    if (_trackIndex == null)
                    {
                        _trackIndex = new Dictionary<string, List<long>>();
                        string dbPath = DbPath;
                        if (!File.Exists(dbPath)) return null;
                        IntPtr db;
                        byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
                        if (sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                        try
                        {
                            IntPtr stmt;
                            if (sqlite3_prepare16(db, "SELECT id, jsonStr FROM dbTrack", -1, out stmt, IntPtr.Zero) == 0)
                            {
                                while (sqlite3_step(stmt) == SQLITE_ROW)
                                {
                                    long id = 0;
                                    IntPtr p0 = sqlite3_column_text16(stmt, 0);
                                    if (p0 != IntPtr.Zero) long.TryParse(Marshal.PtrToStringUni(p0), out id);
                                    IntPtr p1 = sqlite3_column_text16(stmt, 1);
                                    string name = p1 == IntPtr.Zero ? "" : ExtractName(Marshal.PtrToStringUni(p1));
                                    if (id > 0 && name.Length > 0)
                                    {
                                        string key = Normalize(name);
                                        List<long> list;
                                        if (!_trackIndex.TryGetValue(key, out list)) { list = new List<long>(); _trackIndex[key] = list; }
                                        if (!list.Contains(id)) list.Add(id);
                                    }
                                }
                                sqlite3_finalize(stmt);
                            }
                        }
                        finally { sqlite3_close(db); }
                    }
                }
                string k = Normalize(title);
                List<long> exact;
                if (_trackIndex != null && _trackIndex.TryGetValue(k, out exact) && exact.Count > 0) return exact;
                if (_trackIndex == null) return null;
                var fuzzy = new List<long>();
                foreach (var kv in _trackIndex)
                    if (kv.Key.Contains(k) || k.Contains(kv.Key)) fuzzy.AddRange(kv.Value);
                return fuzzy.Count > 0 ? fuzzy : null;
            }
            catch { return null; }
        }

        static string ExtractName(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int ni = json.IndexOf("\"name\":\"");
            if (ni < 0) return "";
            int e = json.IndexOf('"', ni + 8);
            if (e <= ni) return "";
            return json.Substring(ni + 8, e - ni - 8);
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        }
    }

    // ------------------------------------------------------------ SMTC 媒体源
    public class SmtcSong
    {
        public string Title;
        public string Artist;
        public string Status;      // Playing / Paused / Stopped / Closed
        public long PosMs;         // 一般拿不到（网易云不上报），>0 时优先用
        public bool HasSession;
    }

    public static class SmtcSource
    {
        static GlobalSystemMediaTransportControlsSession _session;

        public static SmtcSong GetNow()
        {
            try
            {
                if (_session == null)
                {
                    var mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                        .AsTask().GetAwaiter().GetResult();
                    foreach (var s in mgr.GetSessions())
                    {
                        string aumid = s.SourceAppUserModelId ?? "";
                        if (aumid.IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            aumid.IndexOf("netease", StringComparison.OrdinalIgnoreCase) >= 0)
                        { _session = s; break; }
                    }
                }
                if (_session == null) return new SmtcSong { HasSession = false };

                string title = null, artist = null;
                try
                {
                    var mp = _session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    title = mp.Title; artist = mp.Artist;
                }
                catch
                {
                    // 旧系统没有 TryGetMediaPropertiesAsync，反射调用 GetGlobalProperties
                    try
                    {
                        var m = _session.GetType().GetMethod("GetGlobalProperties");
                        if (m != null)
                        {
                            var g = m.Invoke(_session, null);
                            var pt = g.GetType().GetProperty("Title");
                            var pa = g.GetType().GetProperty("Artist");
                            if (pt != null) title = Convert.ToString(pt.GetValue(g, null));
                            if (pa != null) artist = Convert.ToString(pa.GetValue(g, null));
                        }
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(title)) title = null;

                var tl = _session.GetTimelineProperties();
                var pi = _session.GetPlaybackInfo();
                string status = pi != null ? pi.PlaybackStatus.ToString() : "Closed";
                long posMs = (long)tl.Position.TotalMilliseconds;
                return new SmtcSong
                {
                    Title = title,
                    Artist = artist,
                    Status = status,
                    PosMs = posMs > 0 ? posMs : 0,
                    HasSession = true
                };
            }
            catch
            {
                // SMTC 完全不可用（老系统/权限）或会话消失
                _session = null;
                return new SmtcSong { HasSession = false };
            }
        }
    }

    // ------------------------------------------------------- 本地缓存歌曲 id 源
    public static class CacheSource
    {
        public static string CacheDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetEase", "CloudMusic", "Cache", "Cache");
            }
        }

        /// <summary>找当前正在播放的歌曲 id：最新且大小>=MinSize 的 .uc 文件名前缀；
        /// 0 字节是预取忽略。返回 null 表示没有。</summary>
        public static long? GetCurrentId(long minSize = 150 * 1024)
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return null;
                string best = null; DateTime bestTime = DateTime.MinValue; long bestSize = 0;
                foreach (var f in Directory.GetFiles(CacheDir, "*-3999-*.uc"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length < minSize) continue;
                        if (fi.LastWriteTime > bestTime) { bestTime = fi.LastWriteTime; best = fi.Name; bestSize = fi.Length; }
                    }
                    catch { }
                }
                if (best == null) return null;
                int dash = best.IndexOf('-');
                if (dash <= 0) return null;
                long id;
                if (long.TryParse(best.Substring(0, dash), out id) && id > 0) return id;
                return null;
            }
            catch { return null; }
        }

        /// <summary>本地 Temp 歌词缓存：文件名 = MD5(歌曲id)，命中则直接读，避免请求网络</summary>
        public static string TryGetTempLyric(long id)
        {
            try
            {
                string md5 = Md5Hex(id.ToString());
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetEase", "CloudMusic", "Temp", md5);
                if (!File.Exists(p)) return null;
                var fi = new FileInfo(p);
                if (fi.Length > 2 * 1024 * 1024) return null;   // 异常大，忽略
                string json = File.ReadAllText(p, Encoding.UTF8);
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object lrcObj;
                if (!root.TryGetValue("lrc", out lrcObj)) return null;
                var lrc = (Dictionary<string, object>)lrcObj;
                object lyricObj;
                string lyric = lrc.TryGetValue("lyric", out lyricObj) ? Convert.ToString(lyricObj) : "";
                if (string.IsNullOrEmpty(lyric)) return null;
                Log.Write("[本地歌词] 命中 Temp 缓存 " + p);
                return lyric;
            }
            catch { return null; }
        }

        /// <summary>查指定歌曲 id 最新 .uc 缓存文件的写入时间（seek 检测用）</summary>
        public static DateTime? GetCacheMtime(long id)
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return null;
                DateTime? best = null;
                foreach (var f in Directory.GetFiles(CacheDir, id + "-3999-*.uc"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length < 100 * 1024) continue;   // 忽略 0 字节预取
                        if (!best.HasValue || fi.LastWriteTime > best.Value) best = fi.LastWriteTime;
                    }
                    catch { }
                }
                return best;
            }
            catch { return null; }
        }

        public static string Md5Hex(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(32);
                foreach (byte x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // ------------------------------------------------------------- 歌词引擎
    public class DisplayState
    {
        public string Text = "";
        public double KtvProgress = -1;   // -1 表示不画逐字高亮
        public bool Dimmed;
        public string Debug = "";
    }

    public class LyricsEngine
    {
        string _key;                 // 当前歌曲标识："id:xxx" 或 "t:歌名|歌手"
        long? _id;
        DateTime _anchorUtc;         // 检测到换歌的时刻
        long _pausedTotalMs;         // 累计暂停时长
        DateTime? _pauseStarted;
        DateTime? _lastSeekMtime;    // 上次检测到的 .uc mtime（seek 检测用）
        List<LrcLine> _lines = new List<LrcLine>();
        string _songDisplay = "";    // "歌名 - 歌手"
        int _gen = 0;                // 换歌代数：旧加载线程结果作废
        readonly object _lock = new object();
        int _curLineIdx = -1;        // 当前歌词行（用于跳行对齐）
        DateTime? _seekHintUntil;    // seek 提示显示到何时

        public string LastSongKey { get { return _key; } }
        public string SongDisplay { get { return _songDisplay; } }   // 当前"歌名 - 歌手"（随歌曲更新）

        public DisplayState Poll()
        {
            var st = new DisplayState();
            var smtc = SmtcSource.GetNow();

            // 1) 缓存里的歌曲 id（仅作歌词来源提示，需经歌名校验）
            long? cacheId = CacheSource.GetCurrentId();

            // 2) 换歌检测：SMTC 歌名实时优先；无 SMTC 时才退到缓存 id
            string key = null;
            long? idHint = null;
            if (smtc.HasSession && smtc.Title != null)
            {
                key = "t:" + smtc.Title + "|" + smtc.Artist;
                idHint = cacheId;
            }
            else if (cacheId.HasValue)
            {
                key = "id:" + cacheId.Value;
                idHint = cacheId;
            }

            if (key == null)
            {
                // 没在播放
                _key = null;
                st.Text = Config.ShowIdleText ? Config.IdleText : "";
                st.Debug = "无网易云会话/缓存";
                return st;
            }

            if (key != _key)
            {
                _key = key;
                _anchorUtc = DateTime.UtcNow;
                _pausedTotalMs = 0;
                _pauseStarted = null;
                if (smtc.HasSession && smtc.Title != null)
                    _songDisplay = string.IsNullOrEmpty(smtc.Artist) ? CleanTitle(smtc.Title) : CleanTitle(smtc.Title) + " - " + smtc.Artist;
                else
                    _songDisplay = "";
                _lines = new List<LrcLine>();
                _curLineIdx = -1;
                _seekHintUntil = null;
                _lastSeekMtime = null;
                st.Text = _songDisplay.Length > 0 ? _songDisplay : "正在加载歌词…";
                st.Debug = "换歌: " + key;
                Log.Write("[换歌] " + key + (idHint.HasValue ? " (缓存id提示 " + idHint.Value + ")" : ""));
                StartLyricLoad(idHint, smtc);
            }

            // 3) 进度
            long pos = 0;
            if (smtc.HasSession && smtc.PosMs > 0)
                pos = smtc.PosMs;                       // 未来如果客户端上报进度，直接用它
            else
            {
                bool paused = smtc.HasSession && smtc.Status == "Paused";
                if (paused)
                {
                    if (_pauseStarted == null) _pauseStarted = DateTime.UtcNow;
                    pos = (long)(_pauseStarted.Value - _anchorUtc).TotalMilliseconds - _pausedTotalMs + Config.OffsetMs;
                    st.Dimmed = Config.DimWhenPaused;
                }
                else
                {
                    if (_pauseStarted != null)
                    {
                        _pausedTotalMs += (long)(DateTime.UtcNow - _pauseStarted.Value).TotalMilliseconds;
                        _pauseStarted = null;
                    }
                    long elapsed = (long)(DateTime.UtcNow - _anchorUtc).TotalMilliseconds - _pausedTotalMs;
                    pos = elapsed + Config.OffsetMs;
                }
            }

            // 4) 取当前行
            List<LrcLine> lines;
            lock (_lock) lines = _lines;
            if (lines.Count > 0)
            {
                double prog;
                int idx = Lrc.FindLine(lines, pos, out prog);
                _curLineIdx = idx;
                if (_seekHintUntil.HasValue && DateTime.UtcNow < _seekHintUntil.Value)
                {
                    st.Text = "♪ 检测到跳转进度，按 Alt+Shift+↑/↓ 对齐歌词";
                    st.KtvProgress = -1;
                    st.Debug = "seek提示";
                }
                else
                {
                    st.Text = lines[idx].Text;
                    st.KtvProgress = Config.KtvMode ? prog : -1;
                    st.Debug = key + " pos=" + pos + "ms line=" + idx + "/" + lines.Count;
                }
            }
            else
            {
                st.Text = _songDisplay.Length > 0 ? _songDisplay : "正在加载歌词…";
                st.Debug = key + " 歌词加载中";
            }
            CheckSeekSignal();
            return st;
        }

        // 手动跳歌词行（用户 seek 后对齐用）：跳到上一句/下一句并把锚点校准到该行
        public void ShiftLine(int delta)
        {
            lock (_lock)
            {
                if (_lines.Count == 0) return;
                int idx = _curLineIdx < 0 ? 0 : _curLineIdx + delta;
                idx = Math.Max(0, Math.Min(idx, _lines.Count - 1));
                _curLineIdx = idx;
                long target = _lines[idx].TimeMs;
                _anchorUtc = DateTime.UtcNow.AddMilliseconds(-(target - Config.OffsetMs));
                _pausedTotalMs = 0;
                _pauseStarted = null;
                _seekHintUntil = null;
                Log.Write("[跳行] 第 " + idx + " 行: " + _lines[idx].Text + " (锚点=" + target + "ms)");
            }
        }

        // 检测 seek：当前歌曲的 .uc 缓存文件 mtime 刷新说明发生了跳转
        void CheckSeekSignal()
        {
            try
            {
                if (!_id.HasValue) return;
                var m = CacheSource.GetCacheMtime(_id.Value);
                if (!m.HasValue) return;
                var age = DateTime.UtcNow - m.Value;
                if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(4))
                {
                    if (!_lastSeekMtime.HasValue || _lastSeekMtime.Value != m.Value)
                    {
                        _lastSeekMtime = m.Value;
                        _seekHintUntil = DateTime.UtcNow.AddSeconds(4);
                        Log.Write("[seek] 检测到播放进度跳转，提示对齐");
                    }
                }
            }
            catch { }
        }

        static bool SameSong(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string x = new string(a.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
            string y = new string(b.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
            return x == y || x.Contains(y) || y.Contains(x);
        }

        // 歌名显示时去掉括号及括号内容（如 "(G.E.M.重生版)"、"（Live）"）
        static readonly Regex ParenRegex = new Regex(@"\s*[\(（][^\)）]*[\)）]", RegexOptions.Compiled);
        static string CleanTitle(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = ParenRegex.Replace(t, "");
            return t.Trim();
        }

        // 用 webdb.dat 播放历史里的 playtime 校准锚点（歌曲真实开始时刻）
        void TryAnchorFromDb(long songId)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var t = NeteaseDb.GetTrackStartUtc(songId);
                    if (t.HasValue)
                    {
                        var age = DateTime.UtcNow - t.Value;
                        // 只接受"刚写入"的播放起点（45 秒内）：预加载切歌延迟可达 30~40 秒；
                        // 太久远的 playtime 是这首歌上次播放的旧记录（反复切同一首歌时客户端不写新记录），
                        // 用了会导致歌词进度错位（不从开头开始）。
                        if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(45))
                        {
                            lock (_lock)
                            {
                                // 确认当前仍在播这首歌才应用
                                if (_id == songId)
                                {
                                    _anchorUtc = t.Value;
                                    _pausedTotalMs = 0;
                                    _pauseStarted = null;
                                }
                            }
                            Log.Write("[校准] 歌曲开始时刻=" + t.Value.ToLocalTime().ToString("HH:mm:ss.fff") +
                                " (距检测 " + age.TotalSeconds.ToString("F1") + "s)");
                        }
                        else
                        {
                            Log.Write("[校准] 忽略旧播放起点 " + age.TotalSeconds.ToString("F1") + "s（非本次播放，歌词从头开始）");
                        }
                    }
                }
                catch { }
            });
        }

        void StartLyricLoad(long? cacheIdHint, SmtcSong smtc)
        {
            int myGen = ++_gen;   // 本次加载的代数；换歌后旧线程结果作废
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    long? id = null;
                    string lrcText = null;
                    long offset = 0;
                    if (smtc != null && smtc.Title != null)
                    {
                        // SMTC 身份为主。歌曲 id 候选链（搜索接口可能被网易云风控，优先本地来源）：
                        // 1) 本地歌曲库 dbTrack 按歌名匹配（快、无风控影响）
                        // 2) 缓存 .uc 前缀（经 song/detail 歌名校验）
                        // 3) 播放历史最新记录（客户端写入有延迟，重试几次）
                        // 4) 搜索接口兜底
                        long? matchedId = null;
                        var ids = NeteaseDb.FindTrackIdsByName(smtc.Title);
                        if (ids != null && ids.Count > 0)
                            matchedId = ids[0];
                        if (matchedId == null && cacheIdHint.HasValue)
                        {
                            var d = NeteaseApi.FetchSongDetail(cacheIdHint.Value);
                            if (d != null && SameSong(smtc.Title, d.Item1))
                                matchedId = cacheIdHint.Value;
                        }
                        for (int attempt = 0; attempt < 3 && matchedId == null; attempt++)
                        {
                            var lt = NeteaseDb.GetLatestTrack();
                            if (lt != null && SameSong(smtc.Title, lt.Item2))
                                matchedId = lt.Item1;
                            if (matchedId == null && attempt < 2)
                                System.Threading.Thread.Sleep(2000 + attempt * 1500);   // 等客户端写入播放历史
                        }
                        if (matchedId == null)
                        {
                            var s = NeteaseApi.SearchSong(smtc.Title, smtc.Artist);
                            if (s != null)
                            {
                                matchedId = s.Item1;
                                if (_songDisplay.Length == 0) _songDisplay = CleanTitle(s.Item2) + " - " + s.Item3;
                            }
                        }
                        id = matchedId;
                    }
                    else if (cacheIdHint.HasValue)
                    {
                        // 没有 SMTC 时直接信缓存 id
                        id = cacheIdHint.Value;
                        var d = NeteaseApi.FetchSongDetail(id.Value);
                        if (d != null && _songDisplay.Length == 0) _songDisplay = CleanTitle(d.Item1) + " - " + d.Item2;
                    }

                    if (id.HasValue)
                    {
                        lock (_lock)
                        {
                            if (myGen != _gen) return;   // 已换歌，旧结果作废
                            _id = id;
                        }
                        // 先试本地 Temp 歌词缓存（文件名 = MD5(歌曲id)），再走 API
                        lrcText = CacheSource.TryGetTempLyric(id.Value);
                        if (lrcText == null)
                        {
                            var r = NeteaseApi.FetchLyric(id.Value);
                            if (r != null) { lrcText = r.Item1; offset = r.Item2; }
                        }
                    }

                    var parsed = lrcText != null
                        ? Lrc.Parse(lrcText, Config.FilterMetaLines, offset)
                        : new List<LrcLine>();
                    lock (_lock)
                    {
                        if (myGen != _gen) return;   // 已换歌，丢弃
                        _lines = parsed;
                    }
                    Log.Write("[歌词] " + _key + " id=" + (id.HasValue ? id.Value.ToString() : "?") +
                        " 行数=" + parsed.Count +
                        (lrcText == null ? " (未获取到，显示歌名)" : ""));
                    if (id.HasValue) TryAnchorFromDb(id.Value);   // 用最终 id 校准播放起点
                }
                catch (Exception e)
                {
                    Log.Write("[歌词] 加载异常: " + e.Message);
                }
            });
        }
    }

    // ---------------------------------------------------------- 任务栏悬浮窗
    // 两种渲染模式：
    //   Solid   —— 不透明实心条（普通顶层窗口 + WM_NCHITTEST 点击穿透），任何环境必然可见，默认
    //   Layered —— UpdateLayeredWindow 分层透明（带半透明底），部分远程/虚拟显示环境不可见
    public class OverlayForm : Form
    {
        readonly LyricsEngine _engine = new LyricsEngine();
        DisplayState _state = new DisplayState();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer _repos = new System.Windows.Forms.Timer();
        Font _font;
        Rectangle _taskbar;
        readonly bool _solid = Config.IsSolid;
        int _solidW = 220;                     // Solid 模式当前条宽
        NotifyIcon _tray;                      // 系统托盘图标
        const int WM_NCHITTEST = 0x84;
        const int HTTRANSPARENT = -1;
        const int WM_HOTKEY = 0x0312;
        const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001, MOD_SHIFT = 0x0004;
        const uint VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_UP = 0x26, VK_DOWN = 0x28;
        const int HOTKEY_OFFSET_MINUS = 1, HOTKEY_OFFSET_PLUS = 2;
        const int HOTKEY_LINE_PREV = 3, HOTKEY_LINE_NEXT = 4;
        IntPtr _hotkeyHwnd = IntPtr.Zero;
        const uint HOTKEY_MODS = MOD_ALT | MOD_SHIFT;   // Alt+Shift 组合（Ctrl+Shift 会被中文输入法拦截）

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "TaskbarLyricsNetease";
            if (_solid) BackColor = Config.BackdropColor;   // 实心模式：窗口自带深色底
            _font = new Font(Config.FontName, Config.FontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            _timer.Interval = Config.PollMs;
            _timer.Tick += OnTick;
            _timer.Start();
            _repos.Interval = 2000;
            _repos.Tick += delegate { Place(); };
            _repos.Start();
            Place();
            SetupTray();
        }

        // 系统托盘图标：右键菜单 设置 / 开关项 / 显示·隐藏歌词 / 退出
        void SetupTray()
        {
            _tray = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "任务栏歌词（网易云）",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("设置（打开 config.ini）", null, delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", Config.ConfigPath); }
                catch { Log.Write("[托盘] 打开配置失败"); }
            });
            menu.Items.Add(new ToolStripSeparator());
            // 字体大小：增大/减小 2px，自动保存并立即生效
            var fontMenu = new ToolStripMenuItem("字体大小");
            fontMenu.DropDownItems.Add("增大 2px", null, delegate
            {
                SetFontSize(Config.FontSize + 2);
                fontMenu.Text = "字体大小（当前 " + (int)Config.FontSize + "）";
            });
            fontMenu.DropDownItems.Add("减小 2px", null, delegate
            {
                SetFontSize(Config.FontSize - 2);
                fontMenu.Text = "字体大小（当前 " + (int)Config.FontSize + "）";
            });
            fontMenu.Text = "字体大小（当前 " + (int)Config.FontSize + "）";
            menu.Items.Add(fontMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MakeToggle("显示歌名-歌手前缀", Config.ShowSongTitle,
                delegate(bool v) { Config.ShowSongTitle = v; }));
            menu.Items.Add(MakeToggle("逐字高亮（KTV）", Config.KtvMode,
                delegate(bool v) { Config.KtvMode = v; }));
            menu.Items.Add(MakeToggle("过滤 作词/作曲 等行", Config.FilterMetaLines,
                delegate(bool v) { Config.FilterMetaLines = v; }));
            menu.Items.Add(MakeToggle("暂停时变暗", Config.DimWhenPaused,
                delegate(bool v) { Config.DimWhenPaused = v; }));
            menu.Items.Add(MakeToggle("没播放时显示提示", Config.ShowIdleText,
                delegate(bool v) { Config.ShowIdleText = v; }));
            menu.Items.Add(MakeToggle("写日志", Config.LogToFile,
                delegate(bool v) { Config.LogToFile = v; }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示/隐藏歌词", null, delegate { Visible = !Visible; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate
            {
                _tray.Visible = false;
                Application.Exit();
            });
            _tray.ContextMenuStrip = menu;
        }

        // 复选开关菜单项：点击切换配置并写回 config.ini，立即刷新显示
        ToolStripMenuItem MakeToggle(string label, bool initial, Action<bool> apply)
        {
            var mi = new ToolStripMenuItem(label);
            mi.CheckOnClick = true;
            mi.Checked = initial;
            mi.Click += delegate
            {
                apply(mi.Checked);
                Config.Save();
                RefreshDisplay();
                Log.Write("[托盘] " + label + " -> " + mi.Checked);
            };
            return mi;
        }

        void RefreshDisplay()
        {
            if (_solid) { FitSolidWidth(); DrawNow(); }
            else Render();
        }

        // 调整字号：重建字体、保存配置、立即刷新显示
        void SetFontSize(float size)
        {
            if (size < 10) size = 10;
            if (size > 40) size = 40;
            Config.FontSize = size;
            if (_font != null) _font.Dispose();
            _font = new Font(Config.FontName, Config.FontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            Config.Save();
            RefreshDisplay();
            Log.Write("[托盘] 字号 -> " + size.ToString("0"));
        }

        // 代码画一个音符托盘图标
        static Icon CreateTrayIcon()
        {
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bg = new SolidBrush(Color.FromArgb(40, 110, 190)))
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    using (var f = new Font("Segoe UI Symbol", 22f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var br = new SolidBrush(Color.White))
                        g.DrawString("♪", f, br, 6, 3);
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(hIcon))
                        return (Icon)icon.Clone();
                }
                finally { Native.DestroyIcon(hIcon); }
            }
        }

        // Layered 模式需要分层样式；点击穿透两种模式都通过 WndProc 的 WM_NCHITTEST 实现
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                if (!_solid) cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 热键：Alt+Shift+←/→ 微调进度偏移；Alt+Shift+↑/↓ 跳上一句/下一句歌词
            _hotkeyHwnd = Handle;
            bool r1 = Native.RegisterHotKey(_hotkeyHwnd, HOTKEY_OFFSET_MINUS, HOTKEY_MODS, VK_LEFT);
            bool r2 = Native.RegisterHotKey(_hotkeyHwnd, HOTKEY_OFFSET_PLUS, HOTKEY_MODS, VK_RIGHT);
            bool r3 = Native.RegisterHotKey(_hotkeyHwnd, HOTKEY_LINE_PREV, HOTKEY_MODS, VK_UP);
            bool r4 = Native.RegisterHotKey(_hotkeyHwnd, HOTKEY_LINE_NEXT, HOTKEY_MODS, VK_DOWN);
            Log.Write("[热键] 注册 Alt+Shift ←/→=" + r1 + "/" + r2 + " ↑/↓=" + r3 + "/" + r4);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_hotkeyHwnd != IntPtr.Zero)
            {
                Native.UnregisterHotKey(_hotkeyHwnd, HOTKEY_OFFSET_MINUS);
                Native.UnregisterHotKey(_hotkeyHwnd, HOTKEY_OFFSET_PLUS);
                Native.UnregisterHotKey(_hotkeyHwnd, HOTKEY_LINE_PREV);
                Native.UnregisterHotKey(_hotkeyHwnd, HOTKEY_LINE_NEXT);
                _hotkeyHwnd = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_OFFSET_MINUS) { Config.OffsetMs -= 500; Log.Write("[热键] 歌词提前 500ms，OffsetMs=" + Config.OffsetMs); }
                else if (id == HOTKEY_OFFSET_PLUS) { Config.OffsetMs += 500; Log.Write("[热键] 歌词延后 500ms，OffsetMs=" + Config.OffsetMs); }
                else if (id == HOTKEY_LINE_PREV) { _engine.ShiftLine(-1); }
                else if (id == HOTKEY_LINE_NEXT) { _engine.ShiftLine(+1); }
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;   // 鼠标点击穿透到下面的任务栏
                return;
            }
            base.WndProc(ref m);
        }

        void Place()
        {
            var r = Native.GetTaskbarRect();
            if (r.HasValue)
                _taskbar = r.Value;
            else
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                _taskbar = new Rectangle(wa.Left, wa.Bottom, wa.Width, Screen.PrimaryScreen.Bounds.Height - wa.Height);
            }
            int w = _solid ? Math.Max(_solidW, 220) : _taskbar.Width;
            if (_solid)
            {
                int maxW = MaxTrayWidth();
                w = Math.Min(w, maxW);
                int inset = Config.TrayInsetV;
                Bounds = new Rectangle(_taskbar.Left + Math.Max(0, Config.PaddingX - 8),
                    _taskbar.Top + inset, w, Math.Max(20, _taskbar.Height - 2 * inset));
            }
            else
            {
                Bounds = new Rectangle(_taskbar.Left, _taskbar.Top, _taskbar.Width, _taskbar.Height);
            }
        }

        void OnTick(object s, EventArgs e)
        {
            var st = _engine.Poll();
            bool changed = st.Text != _state.Text || st.Dimmed != _state.Dimmed ||
                (st.KtvProgress >= 0 && Math.Abs(st.KtvProgress - _state.KtvProgress) > 0.02) ||
                (st.KtvProgress < 0 && _state.KtvProgress >= 0);
            _state = st;
            if (changed)
            {
                if (_solid) { FitSolidWidth(); DrawNow(); }
                else Render();
            }
            // 保持窗口在置顶层最上面（点击任务栏时 Explorer 会提升任务栏盖住我们，这里把它压回去）
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // 歌名前缀：始终显示完整的"歌名 - 歌手"
        string BuildPrefix()
        {
            if (!Config.ShowSongTitle) return "";
            return _engine.SongDisplay;
        }

        // Solid 模式：直接用 CreateGraphics 画到窗口 DC（不依赖 Paint 消息管线）
        void DrawNow()
        {
            if (!IsHandleCreated) return;
            try
            {
                using (var g = CreateGraphics())
                {
                    g.Clear(Config.BackdropColor);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    string text = _state.Text ?? "";
                    if (text.Length > 0)
                    {
                        float th = _font.GetHeight(g);
                        float y = (ClientSize.Height - th) / 2f;
                        float x = 8;
                        // 歌名-歌手 前缀（可开关；前缀不参与 KTV 高亮）
                        string prefix = BuildPrefix();
                        if (prefix.Length > 0)
                        {
                            prefix += " | ";
                            using (var pb = new SolidBrush(Config.TextColor))
                                g.DrawString(prefix, _font, pb, x, y);
                            x += g.MeasureString(prefix, _font).Width;
                        }
                        DrawLyric(g, text, x, y);
                    }
                }
            }
            catch (Exception ex) { Log.Write("[绘制] 异常: " + ex.Message); }
        }

        // 带歌名前缀的完整文字宽度（用于窗口宽度自适应）
        float MeasureFullWidth(Graphics g, string text)
        {
            float w = g.MeasureString(text, _font).Width;
            string prefix = BuildPrefix();
            if (prefix.Length > 0)
                w += g.MeasureString(prefix + " | ", _font).Width;
            return w;
        }

        // Solid 模式：按文字宽度自适应条带宽度（量化到 16px 步长，减少重定位）
        void FitSolidWidth()
        {
            string text = _state.Text ?? "";
            int w;
            using (var g = CreateGraphics())
            {
                float tw = text.Length > 0 ? MeasureFullWidth(g, text) : 0;
                w = (int)tw + 24;
            }
            w = Math.Max(220, Math.Min(w, MaxTrayWidth()));
            w = (int)(Math.Ceiling(w / 16.0) * 16);   // 量化，避免频繁 SetWindowPos
            if (w != _solidW)
            {
                _solidW = w;
                int inset = Config.TrayInsetV;
                Bounds = new Rectangle(_taskbar.Left + Math.Max(0, Config.PaddingX - 8),
                    _taskbar.Top + inset, w, Math.Max(20, _taskbar.Height - 2 * inset));
            }
        }

        // 歌词条最大宽度：TrayMaxWidth>0 用配置值，否则 任务栏宽-900（长歌词也能显示全）
        int MaxTrayWidth()
        {
            if (Config.TrayMaxWidth > 0) return Config.TrayMaxWidth;
            int maxW = _taskbar.Width - 900;
            if (maxW < 300) maxW = _taskbar.Width / 2;
            return maxW;
        }

        // Layered 模式：把歌词画到 ARGB 位图并提交给分层窗口
        void Render()
        {
            if (!IsHandleCreated) return;
            int w = Math.Max(1, Width), h = Math.Max(1, Height);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    string text = _state.Text ?? "";
                    if (text.Length > 0)
                    {
                        float th = _font.GetHeight(g);
                        string prefix = BuildPrefix();
                        if (prefix.Length > 0) prefix += " | ";
                        float fullW = g.MeasureString(text, _font).Width +
                            (prefix.Length > 0 ? g.MeasureString(prefix, _font).Width : 0);
                        float x;
                        if (string.Equals(Config.Align, "Center", StringComparison.OrdinalIgnoreCase))
                            x = (w - fullW) / 2f;
                        else
                            x = Config.PaddingX;
                        float y = (h - th) / 2f;
                        if (Config.BackdropAlpha > 0)
                        {
                            using (var bb = new SolidBrush(Color.FromArgb(Config.BackdropAlpha, 0, 0, 0)))
                                g.FillRectangle(bb, x - 6, y - 1, fullW + 12, th + 2);
                        }
                        if (prefix.Length > 0)
                        {
                            using (var pb = new SolidBrush(Config.TextColor))
                                g.DrawString(prefix, _font, pb, x, y);
                            x += g.MeasureString(prefix, _font).Width;
                        }
                        DrawLyric(g, text, x, y);
                    }
                }
                UpdateLayered(bmp, Left, Top, w, h);
            }
        }

        // Solid 模式：直接画在窗口上（背景由 BackColor 提供）
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_solid) return;
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string text = _state.Text ?? "";
            if (text.Length == 0) return;
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                float th = _font.GetHeight(g);
                float x = 8;
                float y = (ClientSize.Height - th) / 2f;
                DrawLyric(g, text, x, y);
            }
        }

        void DrawLyric(Graphics g, string text, float x, float y)
        {
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                Color main = _state.Dimmed ? Config.DimColor : Config.TextColor;
                if (_state.KtvProgress >= 0 && _state.KtvProgress < 1 && text.Length > 0)
                {
                    using (var sb = new SolidBrush(Config.DimColor))
                        g.DrawString(text, _font, sb, x, y, sf);
                    int k = Math.Max(1, (int)Math.Floor(text.Length * _state.KtvProgress + 0.001));
                    k = Math.Min(k, text.Length);
                    using (var br = new SolidBrush(main))
                        g.DrawString(text.Substring(0, k), _font, br, x, y, sf);
                }
                else
                {
                    using (var br = new SolidBrush(main))
                        g.DrawString(text, _font, br, x, y, sf);
                }
            }
        }

        void UpdateLayered(Bitmap bmp, int x, int y, int w, int h)
        {
            IntPtr hdcScreen = Native.GetDC(IntPtr.Zero);
            IntPtr hdcMem = Native.CreateCompatibleDC(hdcScreen);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(hdcMem, hBitmap);
            var ptDst = new Native.POINT { X = x, Y = y };
            var sz = new Native.SIZE { W = w, H = h };
            var ptSrc = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };
            Native.UpdateLayeredWindow(Handle, hdcScreen, ref ptDst, ref sz, hdcMem, ref ptSrc, 0, ref blend, Native.ULW_ALPHA);
            Native.SelectObject(hdcMem, old);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(hdcMem);
            Native.ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop(); _repos.Stop();
                _timer.Dispose(); _repos.Dispose();
                if (_font != null) _font.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------- 入口
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Config.Init();

            bool wantConsole = args.Any(a => a == "--console");
            bool probe = args.Any(a => a == "--probe");
            if (!wantConsole && !probe) Native.HideConsole();

            bool created;
            using (new Mutex(true, "TaskbarLyricsNetease_SingleInstance", out created))
            {
                if (!created)
                {
                    Log.Write("已有一个实例在运行，退出。");
                    return;
                }
                if (probe) { Probe.Run(); return; }
                Log.Write("启动：配置 " + Config.ConfigPath);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OverlayForm());
            }
        }

        // ---------------------------------------------------------------- 自检
        public static class Probe
        {
            public static void Run()
            {
                Console.WriteLine("===== TaskbarLyricsNetease 自检 =====");
                Console.WriteLine("系统: " + Environment.OSVersion.VersionString);
                var tb = Native.GetTaskbarRect();
                Console.WriteLine("任务栏矩形: " + (tb.HasValue ? tb.Value.ToString() : "未找到 Shell_TrayWnd"));

                Console.WriteLine("\n-- SMTC 媒体会话 --");
                try
                {
                    var mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                        .AsTask().GetAwaiter().GetResult();
                    int n = 0;
                    foreach (var s in mgr.GetSessions())
                    {
                        n++;
                        try
                        {
                            var mp = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                            var tl = s.GetTimelineProperties();
                            var pi = s.GetPlaybackInfo();
                            Console.WriteLine("会话" + n + ": AUMID=" + s.SourceAppUserModelId +
                                " | " + mp.Title + " - " + mp.Artist +
                                " | Pos=" + tl.Position + " End=" + tl.EndTime +
                                " | Status=" + (pi != null ? pi.PlaybackStatus.ToString() : "null"));
                        }
                        catch (Exception e2) { Console.WriteLine("会话" + n + " 读取失败: " + e2.Message); }
                    }
                    if (n == 0) Console.WriteLine("（没有媒体会话，网易云可能没在播放）");
                }
                catch (Exception e) { Console.WriteLine("SMTC 不可用: " + e.Message); }

                Console.WriteLine("\n-- 本地缓存歌曲 id --");
                long? cid = CacheSource.GetCurrentId();
                Console.WriteLine("Cache\\Cache 最新有效 .uc 前缀歌曲id: " + (cid.HasValue ? cid.Value.ToString() : "无"));
                if (cid.HasValue)
                {
                    var d = NeteaseApi.FetchSongDetail(cid.Value);
                    Console.WriteLine("该 id 的歌: " + (d != null ? d.Item1 + " - " + d.Item2 : "查询失败"));
                    string tmp = CacheSource.TryGetTempLyric(cid.Value);
                    Console.WriteLine("Temp 歌词缓存命中: " + (tmp != null ? "是(" + tmp.Length + " 字符)" : "否"));
                }

                Console.WriteLine("\n-- API 测试：搜索《旋木 王菲》 --");
                var s2 = NeteaseApi.SearchSong("旋木", "王菲");
                if (s2 != null)
                {
                    Console.WriteLine("搜索结果: id=" + s2.Item1 + " " + s2.Item2 + " - " + s2.Item3);
                    var ly = NeteaseApi.FetchLyric(s2.Item1);
                    if (ly != null)
                    {
                        var lines = Lrc.Parse(ly.Item1, Config.FilterMetaLines, ly.Item2);
                        Console.WriteLine("歌词行数: " + lines.Count + " offset=" + ly.Item2);
                        if (lines.Count > 0)
                        {
                            Console.WriteLine("前3行:");
                            for (int i = 0; i < Math.Min(3, lines.Count); i++)
                                Console.WriteLine("  " + lines[i].TimeMs + "ms " + lines[i].Text);
                        }
                    }
                    else Console.WriteLine("歌词获取失败（可能需要网络）");
                }
                else Console.WriteLine("搜索失败（可能需要网络）");

                Console.WriteLine("\n-- 配置 --");
                Console.WriteLine("config.ini: " + Config.ConfigPath);
                Console.WriteLine("FontSize=" + Config.FontSize + " Align=" + Config.Align +
                    " PaddingX=" + Config.PaddingX + " OffsetMs=" + Config.OffsetMs +
                    " KtvMode=" + Config.KtvMode);
                Console.WriteLine("\n自检完成。");
            }
        }
    }
}
