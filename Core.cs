using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Sources;

namespace FiveMManager;

public class Account { public string Name { get; set; } = ""; public string LastUsed { get; set; } = ""; }
public class Server
{
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
    public int Pure { get; set; } = 0;       // 0 = off, else pure mode level 1-3
    public string Build { get; set; } = "";  // game build, e.g. "2802"; blank = server default

    [JsonIgnore] public string Summary =>
        string.Join("   ", new[] { Link, Pure > 0 ? $"pure {Pure}" : "", Build.Length > 0 ? $"b{Build}" : "" }
            .Where(x => x.Length > 0));
}

public class Config
{
    public string FiveMAppPath { get; set; } = Core.DefaultFiveMPath();
    // Glob patterns (relative to FiveMAppPath). Last path segment may contain * or ?.
    // ros_* = the Rockstar/ROS account link; version suffixes change on FiveM updates, so wildcard it.
    public List<string> LinkedPaths { get; set; } = new() { @"data\game-storage\ros_*" };
    public List<Account> Accounts { get; set; } = new();
    public List<Server> Servers { get; set; } = new();
    // Programs/files opened alongside FiveM on every launch. Full paths, one per entry.
    public List<string> LaunchWith { get; set; } = new();
    public string ActiveAccount { get; set; } = "";
    public int BootDelaySeconds { get; set; } = 30;   // wait after opening FiveM (build/pure) before firing the connect URI
}

public static class Core
{
    static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FiveMManager");
    static string ConfigPath => Path.Combine(AppDir, "config.json");
    static string ProfilesDir => Path.Combine(AppDir, "profiles");

    public static string DefaultFiveMPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.app");

    // ---- config ----
    public static Config Load()
    {
        try { if (File.Exists(ConfigPath)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new(); }
        catch { /* corrupt config -> start fresh rather than crash */ }
        return new();
    }

    public static void Save(Config c)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string SafeName(string n) => string.Concat(n.Split(Path.GetInvalidFileNameChars()));
    static string ProfileDir(string name) => Path.Combine(ProfilesDir, SafeName(name));

    // ---- account profile ops (public wrappers over pure helpers) ----
    public static void SnapshotAccount(Config c, string name) => Snapshot(c.FiveMAppPath, ProfileDir(name), c.LinkedPaths);
    public static void RestoreAccount(Config c, string name) => Restore(ProfileDir(name), c.FiveMAppPath, c.LinkedPaths);
    public static void Unlink(Config c) => Delete(c.FiveMAppPath, c.LinkedPaths);
    public static void DeleteProfile(string name) => DeletePath(ProfileDir(name));

    public static bool IsFiveMRunning()
    {
        var self = Process.GetCurrentProcess().ProcessName; // "FiveMManager" also contains "FiveM" — don't match ourselves
        return Process.GetProcesses().Any(p =>
        {
            try { return !p.ProcessName.Equals(self, StringComparison.OrdinalIgnoreCase)
                       && p.ProcessName.Contains("FiveM", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
    }

    // ---- launching ----
    public static string NormalizeTarget(string link)
    {
        link = link.Trim();
        if (link.Length == 0) return link;
        if (link.StartsWith("fivem://", StringComparison.OrdinalIgnoreCase)) return link;
        // strip http(s):// so cfx.re/join/<code> and bare ip:port both ride the fivem:// protocol
        foreach (var pre in new[] { "https://", "http://" })
            if (link.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { link = link[pre.Length..]; break; }
        return "fivem://connect/" + link;
    }

    // FiveM checks the PARENT process of a launch and rejects anything that isn't the shell/a browser
    // ("launch from shell or web browser") — so we never spawn FiveM directly from our process; the
    // shell (explorer.exe) always does it.
    public static void Connect(string uri) =>
        Process.Start(new ProcessStartInfo("explorer.exe", uri) { UseShellExecute = true });

    // Returns the connect URI the CALLER must fire once the client has booted, or null when the join
    // was already handed to the shell and nothing further is needed.
    //
    // Build / pure are FiveM.exe launch flags that can't ride the URI, and combining flags + URI in one
    // launch opens the game but won't join. So mirror the working .bat: open FiveM with the flags (via a
    // temp shortcut explorer runs, so the parent guard is satisfied), let it boot, THEN fire the connect
    // URI at the already-running client — no rebuild since it's already on that build. The wait lives in
    // the UI so it can show a countdown and let you join early.
    // Opens everything in LaunchWith. UseShellExecute so an .exe, a .lnk, a document or a URL all
    // work. Returns the ones that wouldn't open - a bad entry here must never stop you joining.
    public static List<string> LaunchExtras(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.Trim();
            if (path.Length == 0) continue;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { failed.Add(Path.GetFileName(path.TrimEnd('\\', '/'))); }
        }
        return failed;
    }

    public static string? Launch(Server s, Config c)
    {
        var uri = NormalizeTarget(s.Link);
        if (s.Pure <= 0 && string.IsNullOrWhiteSpace(s.Build))
        {
            // No client flags: hand the fivem:// URI to the shell. Server enforces build/pure on connect.
            Connect(uri);
            return null;
        }
        var exe = Path.Combine(Directory.GetParent(c.FiveMAppPath.TrimEnd('\\', '/'))!.FullName, "FiveM.exe");
        var flags = $"-pure_{(s.Pure > 0 ? s.Pure : 0)}";          // .bat passes -pure_0 explicitly even when off
        if (!string.IsNullOrWhiteSpace(s.Build)) flags += $" -b{s.Build.Trim().TrimStart('b', 'B')}";
        LaunchViaShortcut(exe, flags);
        return uri;
    }

    // ponytail: a .lnk that explorer launches is how a normal desktop shortcut passes -b/-pure to FiveM
    // without tripping the "launch from shell" guard. If a future FiveM tightens the parent check, this
    // is the knob to revisit.
    static void LaunchViaShortcut(string exe, string args)
    {
        var lnk = Path.Combine(Path.GetTempPath(), "FiveMManager_join.lnk");
        WriteShortcut(lnk, exe, args);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lnk}\"") { UseShellExecute = true });
    }

    // WScript.Shell COM avoids pulling in a shortcut library for what is two property assignments.
    static void WriteShortcut(string lnkPath, string target, string args = "")
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = target;
        sc.Arguments = args;
        sc.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
        sc.Save();
    }

    // Per-user Start menu, so no admin prompt. Points at whichever exe is running, which means clicking
    // it again after moving the app just repoints the shortcut. The icon comes from the exe itself.
    public static string CreateStartMenuShortcut()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("could not determine the running .exe path");
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "FiveM Manager.lnk");
        WriteShortcut(lnk, exe);
        return lnk;
    }

    // ---- updates ----
    // Where releases live. Not a setting: an app that lets you repoint its own update feed is an app
    // that can be pointed at someone else's build.
    public const string UpdateRepo = "https://github.com/zuhmoon/fivem-manager";

    // Null when this copy wasn't installed by Velopack (a dev build, or the portable zip) - there is
    // no update channel to talk to, and asking anyway throws.
    public static UpdateManager? Updater()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(UpdateRepo, null, false));
            return mgr.IsInstalled ? mgr : null;
        }
        catch (Exception ex) { LogUpdateError(ex); return null; }
    }

    // An update that silently never arrives is the worst failure this app has - it looks like nothing
    // is wrong forever. Leave a trail next to the config.
    public static void LogUpdateError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.AppendAllText(Path.Combine(AppDir, "update.log"), $"{DateTime.Now:s}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never be the thing that breaks the app */ }
    }

    public static string CurrentVersion =>
        Updater()?.CurrentVersion?.ToString()
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "dev";

    // ---- cache ----
    // Disposable client caches under FiveM.app\data. Deliberately NOT here: game-storage (holds the
    // ros_* Rockstar link this whole app manages), citizen, crashes, logs.
    // nui-storage is the CEF profile — ~99% http/gpu/code cache, plus a few hundred KB of Local Storage
    // and IndexedDB where NUI scripts keep UI prefs. Clearing it resets those prefs, nothing more.
    public static readonly string[] CacheFolders = { "cache", "server-cache", "server-cache-priv", "nui-storage" };

    // Returns bytes reclaimed, plus the names of any folders that wouldn't delete (usually a file still
    // locked by a client that hasn't fully exited) — a partial clear is worth reporting, not throwing away.
    public static (long Freed, List<string> Failed) ClearCache(string fivemAppPath)
    {
        long freed = 0;
        var failed = new List<string>();
        foreach (var name in CacheFolders)
        {
            var dir = Path.Combine(fivemAppPath, "data", name);
            if (!Directory.Exists(dir)) continue;
            var size = DirSize(dir);
            try { DeletePath(dir); freed += size; }
            catch { failed.Add(name); }
        }
        return (freed, failed);
    }

    static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }   // a file vanishing mid-walk shouldn't abort the clear
    }

    // ---- pure path helpers (snapshot/restore/delete by glob) ----
    static void Snapshot(string srcRoot, string profileDir, IEnumerable<string> patterns)
    {
        DeletePath(profileDir);
        Directory.CreateDirectory(profileDir);
        foreach (var pat in patterns)
            foreach (var rel in ExpandRelative(srcRoot, pat))
                CopyPath(Path.Combine(srcRoot, rel), Path.Combine(profileDir, rel));
    }

    static void Restore(string profileDir, string fivemRoot, IEnumerable<string> patterns)
    {
        foreach (var pat in patterns)
            foreach (var rel in ExpandRelative(fivemRoot, pat))   // clear current
                DeletePath(Path.Combine(fivemRoot, rel));
        foreach (var pat in patterns)
            foreach (var rel in ExpandRelative(profileDir, pat))  // restore saved
                CopyPath(Path.Combine(profileDir, rel), Path.Combine(fivemRoot, rel));
    }

    static void Delete(string root, IEnumerable<string> patterns)
    {
        foreach (var pat in patterns)
            foreach (var rel in ExpandRelative(root, pat))
                DeletePath(Path.Combine(root, rel));
    }

    // Return existing matches (relative to root). Wildcards allowed only in the last segment.
    static IEnumerable<string> ExpandRelative(string root, string pattern)
    {
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
        {
            var full = Path.Combine(root, pattern);
            if (Directory.Exists(full) || File.Exists(full)) yield return pattern;
            yield break;
        }
        var parentRel = Path.GetDirectoryName(pattern) ?? "";
        var leaf = Path.GetFileName(pattern);
        var parentFull = Path.Combine(root, parentRel);
        if (!Directory.Exists(parentFull)) yield break;
        foreach (var e in Directory.GetFileSystemEntries(parentFull, leaf))
            yield return Path.Combine(parentRel, Path.GetFileName(e));
    }

    static void CopyPath(string src, string dst)
    {
        if (Directory.Exists(src)) CopyDir(src, dst);
        else if (File.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(src, dst, true); }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static void DeletePath(string p)
    {
        if (Directory.Exists(p)) Directory.Delete(p, true);
        else if (File.Exists(p)) File.Delete(p);
    }

    // ponytail: one runnable check for the non-trivial bits (launch normalize + glob snapshot/restore round-trip)
    public static void SelfTest()
    {
        void Eq(string a, string b) { if (a != b) throw new Exception($"expected '{b}' got '{a}'"); }
        Eq(NormalizeTarget("fivem://connect/1.2.3.4"), "fivem://connect/1.2.3.4");
        Eq(NormalizeTarget("https://cfx.re/join/abcde"), "fivem://connect/cfx.re/join/abcde");
        Eq(NormalizeTarget("cfx.re/join/abcde"), "fivem://connect/cfx.re/join/abcde");
        Eq(NormalizeTarget("1.2.3.4:30120"), "fivem://connect/1.2.3.4:30120");

        var tmp = Path.Combine(Path.GetTempPath(), "fmm_selftest_" + Guid.NewGuid().ToString("N"));
        var fivem = Path.Combine(tmp, "fivem");
        var prof = Path.Combine(tmp, "profile");
        var pats = new[] { @"data\game-storage\ros_*" };
        try
        {
            var rosDir = Path.Combine(fivem, @"data\game-storage\ros_2090");
            Directory.CreateDirectory(rosDir);
            File.WriteAllText(Path.Combine(rosDir, "auth.dat"), "token");
            // a heavy cache that must NOT be captured
            var cacheDir = Path.Combine(fivem, @"data\game-storage");
            File.WriteAllText(Path.Combine(cacheDir, "GTA5.exe_huge"), "ignore");

            Snapshot(fivem, prof, pats);
            if (File.Exists(Path.Combine(prof, @"data\game-storage\GTA5.exe_huge"))) throw new Exception("snapshot grabbed cache");
            Delete(fivem, pats);
            if (Directory.Exists(rosDir)) throw new Exception("delete failed");
            Restore(prof, fivem, pats);
            Eq(File.ReadAllText(Path.Combine(rosDir, "auth.dat")), "token");

            // ClearCache must take every cache folder and leave the Rockstar link standing
            var app = Path.Combine(tmp, "app");
            foreach (var n in CacheFolders)
            {
                Directory.CreateDirectory(Path.Combine(app, "data", n));
                File.WriteAllText(Path.Combine(app, "data", n, "junk.bin"), new string('x', 1024));
            }
            var keep = Path.Combine(app, @"data\game-storage\ros_2090");
            Directory.CreateDirectory(keep);
            File.WriteAllText(Path.Combine(keep, "auth.dat"), "token");

            var (freed, failed) = ClearCache(app);
            if (failed.Count > 0) throw new Exception("clear failed on: " + string.Join(",", failed));
            if (freed < 1024 * CacheFolders.Length) throw new Exception($"only {freed} bytes reported freed");
            foreach (var n in CacheFolders)
                if (Directory.Exists(Path.Combine(app, "data", n))) throw new Exception($"{n} survived the clear");
            if (!File.Exists(Path.Combine(keep, "auth.dat"))) throw new Exception("clear cache ate game-storage");

            // one bad "open with FiveM" entry must be reported, not thrown, and blanks ignored
            var extras = LaunchExtras(new[] { "", "   ", Path.Combine(tmp, "definitely-not-here.exe") });
            if (extras.Count != 1) throw new Exception($"expected 1 failed extra, got {extras.Count}");

            // the COM shortcut writer both the joiner and the Start menu entry depend on
            var lnk = Path.Combine(tmp, "probe.lnk");
            WriteShortcut(lnk, Environment.ProcessPath ?? Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            if (!File.Exists(lnk)) throw new Exception("WScript.Shell wrote no shortcut");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }
}
