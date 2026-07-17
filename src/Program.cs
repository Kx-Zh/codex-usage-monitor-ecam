using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexEcamMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\CodexEcamMonitor.Native", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MonitorForm());
            }
        }
    }

    internal sealed class UsageSnapshot
    {
        public double Used;
        public double Remaining;
        public string WindowLabel = "DATA";
        public string ResetLabel = "WAITING FOR CODEX";
        public string AuxiliaryLabel = "";
        public int ResetCredits;
        public long ContextTokens;
        public long ContextWindow;
        public DateTime UpdatedAt;
    }

    internal sealed class ContextUsage
    {
        public long Tokens;
        public long Window;
        public string ConversationId = "";
        public string SessionPath = "";
    }

    internal static class CodexPathResolver
    {
        public static string ResolveHome()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(Normalize(configured));
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        private static string Normalize(string value)
        {
            string result = Environment.ExpandEnvironmentVariables(value.Trim());
            if (result.Length >= 2 && result[0] == '"' && result[result.Length - 1] == '"')
                result = result.Substring(1, result.Length - 2);
            return result;
        }
    }

    // Token data is not part of account/rateLimits/read. Codex records it in
    // the focused task's rollout JSONL, identified through Codex Desktop logs.
    internal static class LocalTokenReader
    {
        private const int MaximumTailBytes = 4 * 1024 * 1024;

        public static bool TryRead(out ContextUsage usage)
        {
            return TryReadFromRoots(
                CodexPathResolver.ResolveHome(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                out usage);
        }

        internal static bool TryReadFromRoots(string codexHome, string localAppData, out ContextUsage usage)
        {
            usage = null;
            try
            {
                List<string> sessions = EnumerateSessions(codexHome);
                if (sessions.Count == 0) return false;

                string conversationId = FindFocusedConversationId(localAppData);
                string target = FindSession(sessions, conversationId);
                bool focusedSessionFound = target != null;
                if (target == null)
                    target = sessions
                        .Where(delegate(string path) { return !IsSubagentSession(path); })
                        .OrderByDescending(delegate(string path) { return SafeLastWriteTimeUtc(path); })
                        .FirstOrDefault();
                if (target == null) return false;

                ContextUsage result;
                if (!TryReadTokenCount(target, out result)) return false;
                result.ConversationId = focusedSessionFound
                    ? conversationId
                    : ConversationIdFromFileName(target);
                result.SessionPath = target;
                usage = result;
                return true;
            }
            catch { return false; }
        }

        private static List<string> EnumerateSessions(string codexHome)
        {
            List<string> sessions = new List<string>();
            AddFiles(sessions, Path.Combine(codexHome, "sessions"), "*.jsonl");
            AddFiles(sessions, Path.Combine(codexHome, "archived_sessions"), "*.jsonl");
            return sessions;
        }

        private static void AddFiles(List<string> destination, string root, string pattern)
        {
            try
            {
                if (Directory.Exists(root))
                    destination.AddRange(Directory.GetFiles(root, pattern, SearchOption.AllDirectories));
            }
            catch { }
        }

        private static string FindFocusedConversationId(string localAppData)
        {
            List<string> logs = new List<string>();
            AddFiles(logs, Path.Combine(localAppData, "Codex", "Logs"), "*.log");
            AddFiles(logs, Path.Combine(localAppData, "OpenAI", "Codex", "Logs"), "*.log");

            string packages = Path.Combine(localAppData, "Packages");
            try
            {
                if (Directory.Exists(packages))
                {
                    foreach (string package in Directory.GetDirectories(packages, "OpenAI.Codex_*"))
                        AddFiles(logs, Path.Combine(package, "LocalCache", "Local", "Codex", "Logs"), "*.log");
                }
            }
            catch { }

            string bestConversationId = null;
            DateTime bestEventUtc = DateTime.MinValue;
            foreach (string log in logs.OrderByDescending(delegate(string path) { return SafeLastWriteTimeUtc(path); }))
            {
                DateTime logWriteUtc = SafeLastWriteTimeUtc(log);
                if (bestEventUtc != DateTime.MinValue && logWriteUtc < bestEventUtc) break;
                string[] lines;
                try
                {
                    lines = ReadTail(log, MaximumTailBytes)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
                catch { continue; }

                for (int index = lines.Length - 1; index >= 0; index--)
                {
                    string line = lines[index];
                    if (line.IndexOf("thread_stream_view_activity_changed", StringComparison.Ordinal) < 0 ||
                        line.IndexOf("active=true", StringComparison.Ordinal) < 0)
                        continue;
                    string id = FieldValue(line, "conversationId=");
                    Guid parsed;
                    if (!Guid.TryParse(id, out parsed)) continue;
                    DateTime eventUtc = ParseLogTimestampUtc(line, logWriteUtc);
                    if (eventUtc > bestEventUtc)
                    {
                        bestEventUtc = eventUtc;
                        bestConversationId = id;
                    }
                    break;
                }
            }
            return bestConversationId;
        }

        private static DateTime ParseLogTimestampUtc(string line, DateTime fallback)
        {
            int separator = line.IndexOf(' ');
            if (separator <= 0) return fallback;
            DateTime parsed;
            if (DateTime.TryParse(
                line.Substring(0, separator),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
                return parsed;
            return fallback;
        }

        private static string FieldValue(string line, string marker)
        {
            int start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = start;
            while (end < line.Length && !Char.IsWhiteSpace(line[end])) end++;
            return line.Substring(start, end - start).Trim('"');
        }

        private static string FindSession(List<string> sessions, string conversationId)
        {
            if (String.IsNullOrWhiteSpace(conversationId)) return null;
            string target = sessions
                .Where(delegate(string path)
                {
                    return Path.GetFileName(path).IndexOf(conversationId, StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .OrderByDescending(delegate(string path) { return SafeLastWriteTimeUtc(path); })
                .FirstOrDefault();
            if (target != null) return target;

            foreach (string path in sessions)
            {
                try
                {
                    if (ReadHead(path, 256 * 1024).IndexOf(conversationId, StringComparison.OrdinalIgnoreCase) >= 0)
                        return path;
                }
                catch { }
            }
            return null;
        }

        private static bool IsSubagentSession(string path)
        {
            try
            {
                string metadata = ReadHead(path, 256 * 1024);
                return metadata.IndexOf("\"thread_source\":\"subagent\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    metadata.IndexOf("\"source\":{\"subagent\"", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return true; }
        }

        private static bool TryReadTokenCount(string path, out ContextUsage usage)
        {
            usage = null;
            string[] lines = ReadTail(path, MaximumTailBytes)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            JavaScriptSerializer json = new JavaScriptSerializer();
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                if (lines[index].IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) < 0) continue;
                try
                {
                    Dictionary<string, object> record = json.Deserialize<Dictionary<string, object>>(lines[index]);
                    Dictionary<string, object> payload = Dictionary(record, "payload");
                    Dictionary<string, object> info = Dictionary(payload, "info");
                    Dictionary<string, object> lastUsage = Dictionary(info, "last_token_usage");
                    long tokens = Convert.ToInt64(Number(lastUsage, "total_tokens"));
                    long window = Convert.ToInt64(Number(info, "model_context_window"));
                    if (tokens < 0 || window <= 0) continue;
                    usage = new ContextUsage();
                    usage.Tokens = tokens;
                    usage.Window = window;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static string ReadTail(string path, int maximumBytes)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                int count = (int)Math.Min((long)maximumBytes, stream.Length);
                byte[] buffer = new byte[count];
                stream.Seek(-count, SeekOrigin.End);
                int read = 0;
                while (read < count)
                {
                    int amount = stream.Read(buffer, read, count - read);
                    if (amount == 0) break;
                    read += amount;
                }
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        private static string ReadHead(string path, int maximumBytes)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                int count = (int)Math.Min((long)maximumBytes, stream.Length);
                byte[] buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int amount = stream.Read(buffer, read, count - read);
                    if (amount == 0) break;
                    read += amount;
                }
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        private static DateTime SafeLastWriteTimeUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static string ConversationIdFromFileName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int separator = name.LastIndexOf('-');
            if (separator < 0) return "";
            string possible = name.Substring(Math.Max(0, name.Length - 36));
            Guid parsed;
            return Guid.TryParse(possible, out parsed) ? possible : "";
        }

        private static Dictionary<string, object> Dictionary(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key)) return null;
            return source[key] as Dictionary<string, object>;
        }

        private static double Number(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null) return 0.0;
            return Convert.ToDouble(source[key], CultureInfo.InvariantCulture);
        }
    }

    internal sealed class AppServerClient : IDisposable
    {
        private readonly object sync = new object();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly string root;
        private readonly string isolatedHome;
        private Process process;
        private int nextId;
        private string lastStandardError = "";

        public AppServerClient(string applicationRoot)
        {
            root = applicationRoot;
            isolatedHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexEcamMonitor",
                "codex-home");
            json.MaxJsonLength = Int32.MaxValue;
        }

        public UsageSnapshot QueryUsage(int timeoutMilliseconds)
        {
            lock (sync)
            {
                EnsureStarted(timeoutMilliseconds);
                Dictionary<string, object> result = Call("account/rateLimits/read", null, timeoutMilliseconds);
                Dictionary<string, object> rateLimits = AsDictionary(Get(result, "rateLimits"));
                List<Dictionary<string, object>> windows = new List<Dictionary<string, object>>();
                AddWindow(windows, Get(rateLimits, "primary"));
                AddWindow(windows, Get(rateLimits, "secondary"));
                if (windows.Count == 0) throw new InvalidOperationException("The account returned no rate-limit window.");

                Dictionary<string, object> main = windows
                    .OrderByDescending(delegate(Dictionary<string, object> item) { return Number(Get(item, "windowDurationMins")); })
                    .First();

                UsageSnapshot snapshot = new UsageSnapshot();
                snapshot.Used = Clamp(Number(Get(main, "usedPercent")), 0.0, 100.0);
                snapshot.Remaining = Math.Round(100.0 - snapshot.Used, 0, MidpointRounding.AwayFromZero);
                double minutes = Number(Get(main, "windowDurationMins"));
                snapshot.WindowLabel = FormatWindow(minutes);
                long resetsAt = Convert.ToInt64(Number(Get(main, "resetsAt")));
                DateTime resetLocal = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(resetsAt)
                    .ToLocalTime();
                snapshot.ResetLabel = "RESET " + resetLocal.ToString("ddd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();

                Dictionary<string, object> secondary = windows.FirstOrDefault(delegate(Dictionary<string, object> item) { return !Object.ReferenceEquals(item, main); });
                if (secondary != null)
                {
                    double secondaryRemaining = Math.Round(100.0 - Number(Get(secondary, "usedPercent")), 0);
                    snapshot.AuxiliaryLabel = FormatWindow(Number(Get(secondary, "windowDurationMins"))) + " " + secondaryRemaining.ToString("0", CultureInfo.InvariantCulture) + "%";
                }
                else
                {
                    object plan = Get(rateLimits, "planType");
                    snapshot.AuxiliaryLabel = plan == null ? "CODEX ONLINE" : "PLAN " + Convert.ToString(plan, CultureInfo.InvariantCulture).ToUpperInvariant();
                }

                Dictionary<string, object> resetCredits = AsDictionaryOrNull(Get(result, "rateLimitResetCredits"));
                if (resetCredits != null && Get(resetCredits, "availableCount") != null)
                    snapshot.ResetCredits = Convert.ToInt32(Number(Get(resetCredits, "availableCount")));
                snapshot.UpdatedAt = DateTime.Now;
                return snapshot;
            }
        }

        private void EnsureStarted(int timeoutMilliseconds)
        {
            if (process != null && !process.HasExited) return;
            StopProcess();
            CodexLaunchTarget target = ResolveCodexLaunchTarget();
            PrepareIsolatedHome();

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = target.FileName;
            info.Arguments = target.Arguments;
            info.WorkingDirectory = root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.EnvironmentVariables["CODEX_HOME"] = isolatedHome;
            info.EnvironmentVariables.Remove("WSL_INTEROP");
            info.EnvironmentVariables.Remove("WSL_DISTRO_NAME");
            info.EnvironmentVariables.Remove("WSLENV");

            process = new Process();
            process.StartInfo = info;
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrWhiteSpace(e.Data)) lastStandardError = e.Data;
            };
            if (!process.Start()) throw new InvalidOperationException("Unable to start Codex CLI from " + target.DisplayPath + ".");
            process.BeginErrorReadLine();

            Dictionary<string, object> clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codex_ecam_monitor_native";
            clientInfo["title"] = "Codex ECAM Monitor";
            clientInfo["version"] = "1.0.1";
            Dictionary<string, object> initialize = new Dictionary<string, object>();
            initialize["clientInfo"] = clientInfo;
            Call("initialize", initialize, timeoutMilliseconds);
            Dictionary<string, object> initialized = new Dictionary<string, object>();
            initialized["method"] = "initialized";
            WriteLine(initialized);
        }

        private CodexLaunchTarget ResolveCodexLaunchTarget()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                string configuredPath = NormalizeCandidate(configured);
                if (!File.Exists(configuredPath))
                    throw new FileNotFoundException(
                        "CODEX_CLI_PATH does not point to a file: " + configuredPath,
                        configuredPath);
                return CreateLaunchTarget(configuredPath);
            }

            string sibling = Path.Combine(root, "codex.exe");
            if (File.Exists(sibling)) return CreateLaunchTarget(sibling);

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] names = { "codex.exe", "codex.cmd", "codex.bat" };
            foreach (string rawDirectory in pathValue.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(rawDirectory)) continue;
                string directory = NormalizeCandidate(rawDirectory);
                foreach (string name in names)
                {
                    string candidate;
                    try { candidate = Path.Combine(directory, name); }
                    catch { continue; }
                    if (File.Exists(candidate)) return CreateLaunchTarget(candidate);
                }
            }

            throw new FileNotFoundException(
                "CODEX CLI NOT FOUND - INSTALL IT OR SET CODEX_CLI_PATH. " +
                "See https://github.com/openai/codex for official installation instructions.");
        }

        private static string NormalizeCandidate(string value)
        {
            string trimmed = Environment.ExpandEnvironmentVariables(value.Trim());
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            return trimmed;
        }

        private static CodexLaunchTarget CreateLaunchTarget(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string extension = Path.GetExtension(fullPath);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                string commandProcessor = Environment.GetEnvironmentVariable("COMSPEC");
                if (String.IsNullOrWhiteSpace(commandProcessor))
                    commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                return new CodexLaunchTarget(
                    commandProcessor,
                    "/d /s /c \"\"" + fullPath + "\" app-server --stdio\"",
                    fullPath);
            }

            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Unsupported Codex CLI file type. Use codex.exe, codex.cmd, or codex.bat.");
            return new CodexLaunchTarget(fullPath, "app-server --stdio", fullPath);
        }

        private sealed class CodexLaunchTarget
        {
            public readonly string FileName;
            public readonly string Arguments;
            public readonly string DisplayPath;

            public CodexLaunchTarget(string fileName, string arguments, string displayPath)
            {
                FileName = fileName;
                Arguments = arguments;
                DisplayPath = displayPath;
            }
        }

        private void PrepareIsolatedHome()
        {
            Directory.CreateDirectory(isolatedHome);
            string sourceHome = CodexPathResolver.ResolveHome();
            CopyCredentialIfPresent(sourceHome, "auth.json");
            CopyCredentialIfPresent(sourceHome, ".credentials.json");
        }

        private void CopyCredentialIfPresent(string sourceHome, string fileName)
        {
            string source = Path.Combine(sourceHome, fileName);
            string destination = Path.Combine(isolatedHome, fileName);
            if (File.Exists(source)) File.Copy(source, destination, true);
        }

        private Dictionary<string, object> Call(string method, object parameters, int timeoutMilliseconds)
        {
            int id = ++nextId;
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["method"] = method;
            request["id"] = id;
            if (parameters != null) request["params"] = parameters;
            WriteLine(request);

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                int remaining = Math.Max(1, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
                Task<string> read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(remaining))
                {
                    StopProcess();
                    throw new TimeoutException("Codex RPC '" + method + "' timed out.");
                }
                string line = read.Result;
                if (line == null)
                {
                    string detail = String.IsNullOrWhiteSpace(lastStandardError) ? "" : " " + lastStandardError;
                    StopProcess();
                    throw new IOException("Codex app-server closed its output stream." + detail);
                }
                if (String.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> response;
                try { response = json.DeserializeObject(line) as Dictionary<string, object>; }
                catch { continue; }
                if (response == null || !response.ContainsKey("id")) continue;
                if (Convert.ToInt32(Number(response["id"])) != id) continue;
                if (response.ContainsKey("error") && response["error"] != null)
                    throw new InvalidOperationException(json.Serialize(response["error"]));
                return AsDictionary(Get(response, "result"));
            }
            throw new TimeoutException("Codex RPC '" + method + "' timed out.");
        }

        private void WriteLine(Dictionary<string, object> message)
        {
            if (process == null || process.HasExited) throw new InvalidOperationException("Codex app-server is not running.");
            process.StandardInput.WriteLine(json.Serialize(message));
            process.StandardInput.Flush();
        }

        private static void AddWindow(List<Dictionary<string, object>> windows, object value)
        {
            Dictionary<string, object> window = AsDictionaryOrNull(value);
            if (window != null) windows.Add(window);
        }

        private static object Get(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary == null) throw new InvalidDataException("Unexpected JSON object from Codex.");
            return dictionary;
        }

        private static Dictionary<string, object> AsDictionaryOrNull(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static double Number(object value)
        {
            if (value == null) return 0.0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static string FormatWindow(double minutes)
        {
            if (minutes >= 10080 && minutes % 10080 == 0) return "WEEK";
            if (minutes >= 1440 && minutes % 1440 == 0) return (minutes / 1440).ToString("0", CultureInfo.InvariantCulture) + " DAY";
            if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60).ToString("0", CultureInfo.InvariantCulture) + " HR";
            return minutes.ToString("0", CultureInfo.InvariantCulture) + " MIN";
        }

        public void Dispose()
        {
            lock (sync) StopProcess();
        }

        private void StopProcess()
        {
            Process old = process;
            process = null;
            if (old == null) return;
            try
            {
                if (!old.HasExited)
                {
                    old.Kill();
                    old.WaitForExit(2000);
                }
            }
            catch { }
            try { old.Dispose(); } catch { }
        }
    }

    internal sealed class MonitorForm : Form
    {
        // A320-style left/top sweep: zero begins low on the left and the
        // 100 mark finishes high on the right.
        private const float ScaleStart = 140.0f;
        private const float ScaleSweep = 200.0f;
        private readonly string root;
        private readonly string stateRoot;
        private readonly string positionFile;
        private readonly AppServerClient client;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer contextRefreshTimer;
        private readonly PrivateFontCollection privateFonts = new PrivateFontCollection();
        private FontFamily ecamFamily;
        private Font titleFont;
        private Font scaleFont;
        private Font valueFont;
        private Font unitFont;
        private Font footerFont;
        private Font weekFont;
        private NotifyIcon notifyIcon;
        private Icon currentTrayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayShowItem;
        private ToolStripMenuItem trayHideItem;
        private ToolStripMenuItem trayTopItem;
        private ToolStripMenuItem windowTopItem;
        private UsageSnapshot snapshot = new UsageSnapshot();
        private bool isLive;
        private string lastError = "";
        private int queryRunning;
        private int contextQueryRunning;
        private bool dragging;
        private Point dragOffset;

        private readonly Color white = Color.FromArgb(238, 244, 241);
        private readonly Color green = Color.FromArgb(101, 243, 174);
        private readonly Color cyan = Color.FromArgb(104, 221, 237);
        private readonly Color amber = Color.FromArgb(244, 177, 100);
        private readonly Color red = Color.FromArgb(255, 111, 104);
        private readonly Color dim = Color.FromArgb(104, 115, 114);
        private readonly Color screenBlack = Color.FromArgb(1, 4, 4);
        private readonly Color bezel = Color.FromArgb(38, 42, 40);

        public MonitorForm()
        {
            root = AppDomain.CurrentDomain.BaseDirectory;
            stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEcamMonitor");
            positionFile = Path.Combine(stateRoot, "window-position.txt");
            Directory.CreateDirectory(stateRoot);
            client = new AppServerClient(root);
            LoadFonts();

            Text = "CODEX ECAM";
            ClientSize = new Size(338, 272);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = screenBlack;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.SizeAll;
            SetDefaultPosition();
            RestorePosition();
            BuildContextMenu();
            BuildTrayIcon();

            MouseDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseUp += delegate { dragging = false; };
            KeyDown += OnKeyDown;
            FormClosing += OnClosing;
            Shown += delegate { QueueAllRefreshes(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { QueueRefresh(); };
            refreshTimer.Start();

            contextRefreshTimer = new System.Windows.Forms.Timer();
            contextRefreshTimer.Interval = 5000;
            contextRefreshTimer.Tick += delegate { QueueContextRefresh(); };
            contextRefreshTimer.Start();
        }

        private void LoadFonts()
        {
            string fontPath = Path.Combine(root, "assets", "ECAMFontRegular.ttf");
            if (File.Exists(fontPath))
            {
                privateFonts.AddFontFile(fontPath);
                ecamFamily = privateFonts.Families[0];
            }
            else ecamFamily = new FontFamily("Consolas");
            titleFont = new Font(ecamFamily, 14, FontStyle.Regular, GraphicsUnit.Pixel);
            scaleFont = new Font(ecamFamily, 19, FontStyle.Regular, GraphicsUnit.Pixel);
            valueFont = new Font(ecamFamily, 24, FontStyle.Regular, GraphicsUnit.Pixel);
            unitFont = new Font(ecamFamily, 13, FontStyle.Regular, GraphicsUnit.Pixel);
            footerFont = new Font(ecamFamily, 14, FontStyle.Regular, GraphicsUnit.Pixel);
            weekFont = new Font(ecamFamily, 14, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripItem refresh = menu.Items.Add("Refresh now");
            ToolStripItem hide = menu.Items.Add("Hide to tray");
            windowTopItem = new ToolStripMenuItem("Always on top");
            windowTopItem.Checked = true;
            windowTopItem.CheckOnClick = true;
            menu.Items.Add(windowTopItem);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem exit = menu.Items.Add("Exit");
            refresh.Click += delegate { QueueAllRefreshes(); };
            hide.Click += delegate { HideMonitor(); };
            windowTopItem.CheckedChanged += delegate
            {
                TopMost = windowTopItem.Checked;
                if (trayTopItem != null && trayTopItem.Checked != windowTopItem.Checked)
                    trayTopItem.Checked = windowTopItem.Checked;
            };
            exit.Click += delegate { Close(); };
            ContextMenuStrip = menu;
        }

        private void BuildTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayShowItem = new ToolStripMenuItem("Show monitor");
            trayHideItem = new ToolStripMenuItem("Hide to tray");
            ToolStripItem refresh = trayMenu.Items.Add("Refresh now");
            trayTopItem = new ToolStripMenuItem("Always on top");
            trayTopItem.Checked = TopMost;
            trayTopItem.CheckOnClick = true;
            trayMenu.Items.Insert(0, trayShowItem);
            trayMenu.Items.Insert(1, trayHideItem);
            trayMenu.Items.Add(trayTopItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem exit = trayMenu.Items.Add("Exit");

            trayShowItem.Click += delegate { ShowMonitor(); };
            trayHideItem.Click += delegate { HideMonitor(); };
            refresh.Click += delegate { QueueAllRefreshes(); };
            trayTopItem.CheckedChanged += delegate
            {
                TopMost = trayTopItem.Checked;
                if (windowTopItem != null && windowTopItem.Checked != trayTopItem.Checked)
                    windowTopItem.Checked = trayTopItem.Checked;
            };
            exit.Click += delegate { Close(); };

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = trayMenu;
            notifyIcon.DoubleClick += delegate { ToggleMonitorVisibility(); };
            notifyIcon.Visible = true;
            UpdateTrayMenuState();
            UpdateTrayIcon();
        }

        private void ToggleMonitorVisibility()
        {
            if (Visible) HideMonitor();
            else ShowMonitor();
        }

        private void ShowMonitor()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            UpdateTrayMenuState();
        }

        private void HideMonitor()
        {
            Hide();
            UpdateTrayMenuState();
        }

        private void UpdateTrayMenuState()
        {
            if (trayShowItem != null) trayShowItem.Enabled = !Visible;
            if (trayHideItem != null) trayHideItem.Enabled = Visible;
        }

        private void UpdateTrayIcon()
        {
            if (notifyIcon == null) return;

            bool hasValue = snapshot.UpdatedAt != DateTime.MinValue;
            string number = hasValue
                ? Math.Round(snapshot.Remaining, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                : "--";
            Color iconColor = !isLive
                ? amber
                : (snapshot.Remaining <= 10 ? red : (snapshot.Remaining <= 20 ? amber : green));

            Icon next = CreateTrayNumberIcon(number, iconColor);
            Icon previous = currentTrayIcon;
            currentTrayIcon = next;
            notifyIcon.Icon = next;
            if (previous != null) previous.Dispose();

            string status = isLive ? "ONLINE" : "NO DATA";
            notifyIcon.Text = hasValue
                ? "Codex " + number + "% | " + snapshot.WindowLabel + " | " + status
                : "Codex -- | " + status;
        }

        private Icon CreateTrayNumberIcon(string number, Color color)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush background = new SolidBrush(screenBlack))
            using (Brush foreground = new SolidBrush(color))
            using (Pen border = new Pen(color, 2f))
            using (Font font = new Font(ecamFamily, number.Length >= 3 ? 12f : 16f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat())
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.FillRectangle(background, 0, 0, 32, 32);
                graphics.DrawRectangle(border, 1, 1, 29, 29);
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(number, font, foreground, new RectangleF(1, 1, 30, 29), format);

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon native = Icon.FromHandle(handle))
                        return (Icon)native.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void QueueRefresh()
        {
            if (Interlocked.CompareExchange(ref queryRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UsageSnapshot fresh = client.QueryUsage(30000);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        fresh.ContextTokens = snapshot.ContextTokens;
                        fresh.ContextWindow = snapshot.ContextWindow;
                        snapshot = fresh;
                        isLive = true;
                        lastError = "";
                        Interlocked.Exchange(ref queryRunning, 0);
                        UpdateTrayIcon();
                        Invalidate();
                    });
                }
                catch (Exception error)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        isLive = false;
                        lastError = error.Message;
                        snapshot.ResetLabel = "DATA UNAVAILABLE";
                        Interlocked.Exchange(ref queryRunning, 0);
                        UpdateTrayIcon();
                        Invalidate();
                    });
                }
            });
        }

        private void QueueAllRefreshes()
        {
            QueueRefresh();
            QueueContextRefresh();
        }

        private void QueueContextRefresh()
        {
            if (Interlocked.CompareExchange(ref contextQueryRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ContextUsage context;
                bool success = LocalTokenReader.TryRead(out context);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (success && context != null)
                        {
                            snapshot.ContextTokens = context.Tokens;
                            snapshot.ContextWindow = context.Window;
                            Invalidate();
                        }
                        Interlocked.Exchange(ref contextQueryRunning, 0);
                    });
                }
                catch
                {
                    Interlocked.Exchange(ref contextQueryRunning, 0);
                }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(screenBlack);

            using (Pen outer = new Pen(bezel, 8f)) g.DrawRectangle(outer, 4, 4, 329, 263);
            using (Pen inner = new Pen(Color.FromArgb(68, 76, 72), 1f)) g.DrawRectangle(inner, 9, 9, 319, 253);
            using (Brush whiteBrush = new SolidBrush(white))
            using (Brush greenBrush = new SolidBrush(green))
            using (Brush cyanBrush = new SolidBrush(cyan))
            using (Brush amberBrush = new SolidBrush(amber))
            using (Brush dimBrush = new SolidBrush(dim))
            {
                Brush statusBrush = isLive ? greenBrush : amberBrush;
                g.DrawString("CODEX", titleFont, whiteBrush, 18, 15);
                g.DrawString("AGENTIC USAGE", unitFont, cyanBrush, 18, 31);
                DrawRightAligned(g, isLive ? "ONLINE" : "NO DATA", titleFont, statusBrush, 309f, 17f);
                g.FillEllipse(statusBrush, 315, 20, 6, 6);
                DrawGauge(g, whiteBrush, greenBrush, cyanBrush, dimBrush);
            }
        }

        private void DrawGauge(Graphics g, Brush whiteBrush, Brush greenBrush, Brush cyanBrush, Brush dimBrush)
        {
            const float centerX = 169f;
            const float centerY = 139f;
            const float radius = 86f;
            RectangleF arcRect = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
            using (Pen arc = new Pen(white, 2.5f)) g.DrawArc(arc, arcRect, ScaleStart, ScaleSweep);
            using (Pen low = new Pen(red, 3.5f)) g.DrawArc(low, arcRect, ScaleStart, ScaleSweep * 0.10f);
            using (Pen caution = new Pen(amber, 3.5f)) g.DrawArc(caution, arcRect, ScaleStart + ScaleSweep * 0.10f, ScaleSweep * 0.10f);

            for (int value = 0; value <= 100; value += 10)
            {
                float angle = ScaleStart + value / 100f * ScaleSweep;
                PointF outer = Polar(centerX, centerY, radius + 1f, angle);
                float tickLength = value % 50 == 0 ? 10f : 7f;
                PointF inner = Polar(centerX, centerY, radius - tickLength, angle);
                Color color = value <= 10 ? red : (value <= 20 ? amber : white);
                using (Pen tick = new Pen(color, 2.2f)) g.DrawLine(tick, inner, outer);
            }

            DrawScaleLabel(g, "5", 50, centerX, centerY, radius, whiteBrush);
            DrawScaleLabel(g, "10", 100, centerX, centerY, radius, whiteBrush);

            float needleAngle = ScaleStart + (float)(snapshot.Remaining / 100.0) * ScaleSweep;
            PointF needleStart = Polar(centerX, centerY, 3f, needleAngle);
            PointF needleEnd = Polar(centerX, centerY, radius + 7f, needleAngle);
            Color needleColor = snapshot.Remaining <= 10 ? red : (snapshot.Remaining <= 20 ? amber : green);
            using (Pen needle = new Pen(needleColor, 3.6f))
            using (Brush needleBrush = new SolidBrush(needleColor))
            {
                needle.StartCap = LineCap.Round;
                needle.EndCap = LineCap.Square;
                g.DrawLine(needle, needleStart, needleEnd);
                g.FillEllipse(needleBrush, centerX - 3.5f, centerY - 3.5f, 7f, 7f);

                RectangleF valueRect = new RectangleF(137, 155, 64, 24);
                using (Pen valueBorder = new Pen(Color.FromArgb(64, 82, 82), 1.2f))
                {
                    g.FillRectangle(Brushes.Black, valueRect);
                    g.DrawRectangle(valueBorder, valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height);
                }
                string integerValue = Math.Round(snapshot.Remaining, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
                // DrawVisualCentered measures the actual glyph run on every
                // paint, so 1-, 2-, and 3-digit values share the same center.
                RectangleF visuallyCenteredValueRect = new RectangleF(
                    valueRect.X + 1.5f,
                    valueRect.Y + 2.0f,
                    valueRect.Width,
                    valueRect.Height);
                DrawVisualCentered(g, integerValue, valueFont, needleBrush, visuallyCenteredValueRect, 0f);
            }
            g.DrawString("%", scaleFont, cyanBrush, 205, 160);

            DrawVisualCentered(g, snapshot.WindowLabel, weekFont, whiteBrush, new RectangleF(121, 190, 96, 20), 0f);
            DrawTokenGauge(g, whiteBrush, cyanBrush, dimBrush);
            g.DrawString(snapshot.ResetLabel, footerFont, cyanBrush, 20, 225);
            string auxiliary = String.IsNullOrWhiteSpace(snapshot.AuxiliaryLabel) ? (isLive ? "CODEX ONLINE" : ShortError()) : snapshot.AuxiliaryLabel;
            g.DrawString(auxiliary, unitFont, dimBrush, 20, 244);
            g.DrawString("RST " + snapshot.ResetCredits.ToString(CultureInfo.InvariantCulture), footerFont, greenBrush, 275, 226);
            if (snapshot.UpdatedAt != DateTime.MinValue)
                g.DrawString(snapshot.UpdatedAt.ToString("HH:mm", CultureInfo.InvariantCulture), unitFont, dimBrush, 279, 246);
        }

        private void DrawTokenGauge(Graphics g, Brush whiteBrush, Brush cyanBrush, Brush dimBrush)
        {
            const float centerX = 284f;
            const float centerY = 190f;
            const float radius = 35f;
            const float startAngle = 150f;
            const float sweepAngle = 240f;
            RectangleF arcRect = new RectangleF(
                centerX - radius,
                centerY - radius,
                radius * 2f,
                radius * 2f);

            // A compact 737 EICAS fuel-quantity treatment: the quantity sits
            // inside a 240-degree tank arc, positioned directly above RST.
            g.DrawString("CTX", unitFont, cyanBrush, 272, 181);
            g.DrawString("K", unitFont, cyanBrush, 295, 201);
            string value = snapshot.ContextTokens > 0
                ? Math.Round(snapshot.ContextTokens / 1000.0, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture)
                : "--";
            DrawVisualCentered(g, value, footerFont, whiteBrush, new RectangleF(260, 198, 39, 20), 0f);

            using (Pen rail = new Pen(dim, 5.5f))
            {
                rail.StartCap = LineCap.Flat;
                rail.EndCap = LineCap.Flat;
                g.DrawArc(rail, arcRect, startAngle, sweepAngle);
            }
            double ratio = snapshot.ContextWindow > 0
                ? Math.Max(0.0, Math.Min(1.0, snapshot.ContextTokens / (double)snapshot.ContextWindow))
                : 0.0;
            float fillSweep = sweepAngle * (float)ratio;
            if (fillSweep <= 0f) return;
            using (Pen fuel = new Pen(white, 5.5f))
            {
                fuel.StartCap = LineCap.Flat;
                fuel.EndCap = LineCap.Flat;
                g.DrawArc(fuel, arcRect, startAngle, fillSweep);
            }
        }

        private void DrawScaleLabel(Graphics g, string text, int value, float centerX, float centerY, float radius, Brush brush)
        {
            float angle = ScaleStart + value / 100f * ScaleSweep;
            PointF point = Polar(centerX, centerY, radius - 23f, angle);
            DrawVisualCentered(g, text, scaleFont, brush, new RectangleF(point.X - 18, point.Y - 13, 36, 26), 0f);
        }

        private static PointF Polar(float centerX, float centerY, float radius, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            return new PointF(
                centerX + radius * (float)Math.Cos(radians),
                centerY + radius * (float)Math.Sin(radians));
        }

        private static void DrawRightAligned(Graphics g, string text, Font font, Brush brush, float right, float y)
        {
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                SizeF measured = g.MeasureString(text, font, Int32.MaxValue, format);
                g.DrawString(text, font, brush, right - measured.Width, y, format);
            }
        }

        private static void DrawVisualCentered(Graphics g, string text, Font font, Brush brush, RectangleF rectangle, float verticalAdjustment)
        {
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                SizeF measured = g.MeasureString(text, font, Int32.MaxValue, format);
                float x = rectangle.X + (rectangle.Width - measured.Width) / 2f;
                float y = rectangle.Y + (rectangle.Height - measured.Height) / 2f + verticalAdjustment;
                g.DrawString(text, font, brush, x, y, format);
            }
        }

        private string ShortError()
        {
            if (String.IsNullOrWhiteSpace(lastError)) return "CONNECTING";
            return lastError.Length > 34 ? lastError.Substring(0, 34).ToUpperInvariant() : lastError.ToUpperInvariant();
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragOffset = e.Location;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            Point cursor = Cursor.Position;
            Location = new Point(cursor.X - dragOffset.X, cursor.Y - dragOffset.Y);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.F5) QueueAllRefreshes();
        }

        private void SetDefaultPosition()
        {
            Screen target = Screen.AllScreens[Screen.AllScreens.Length - 1];
            Left = target.WorkingArea.Right - Width - 24;
            Top = target.WorkingArea.Top + 24;
        }

        private void RestorePosition()
        {
            try
            {
                if (!File.Exists(positionFile)) return;
                string[] values = File.ReadAllText(positionFile).Split(',');
                if (values.Length == 2)
                {
                    Left = Int32.Parse(values[0], CultureInfo.InvariantCulture);
                    Top = Int32.Parse(values[1], CultureInfo.InvariantCulture);
                }
            }
            catch { }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            refreshTimer.Stop();
            contextRefreshTimer.Stop();
            try { File.WriteAllText(positionFile, Left.ToString(CultureInfo.InvariantCulture) + "," + Top.ToString(CultureInfo.InvariantCulture)); }
            catch { }
            if (notifyIcon != null) notifyIcon.Visible = false;
            client.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (refreshTimer != null) refreshTimer.Dispose();
                if (contextRefreshTimer != null) contextRefreshTimer.Dispose();
                if (titleFont != null) titleFont.Dispose();
                if (scaleFont != null) scaleFont.Dispose();
                if (valueFont != null) valueFont.Dispose();
                if (unitFont != null) unitFont.Dispose();
                if (footerFont != null) footerFont.Dispose();
                if (weekFont != null) weekFont.Dispose();
                if (notifyIcon != null) notifyIcon.Dispose();
                if (currentTrayIcon != null) currentTrayIcon.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
                privateFonts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
