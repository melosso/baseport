using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Baseport;

public static partial class InstallCli
{
    private const string Unit = "/etc/systemd/system/baseport.service";
    private const string ServiceName = "Baseport";
    private const string LocalService = "*S-1-5-19";
    private const string SeedLine = "Seeded a one-time admin account";
    private const string DefaultUrl = "http://localhost:5000";

    public static readonly FrozenSet<string> Kept = new[]
    {
        "baseport.db", "baseport.db-shm", "baseport.db-wal", "baseport.key", "uploads", "backups", "log", "appsettings.json", ".env"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static string Dir => EnvFile.InstallDirectory();
    private static string Exe => Path.Combine(Dir, OperatingSystem.IsWindows() ? "Baseport.exe" : "Baseport");
    private static string? Command => Environment.GetEnvironmentVariable("BASEPORT_COMMAND");
    private static bool Linux => OperatingSystem.IsLinux();

    public static async Task<int?> RunAsync(string[] args) => args[0] switch
    {
        "update" => Fail("Update through the installed baseport command, or run the installer again."),
        "status" => Status(),
        "start" or "restart" => Control(args),
        "stop" => Stop(args),
        "service" => Service(args),
        "logs" => await LogsAsync(args),
        "doctor" => await DoctorAsync(),
        "uninstall" => Uninstall(args),
        "accounts" or "providers" or "config" => AsOwner(args),
        _ => null
    };

    public static async Task<int> AroundRestoreAsync(string[] args, Func<string[], Task<int>> restore)
    {
        var caller = Environment.GetEnvironmentVariable("BASEPORT_CALLER_DIR") ?? Directory.GetCurrentDirectory();
        var archive = Array.FindIndex(args, 1, a => !a.StartsWith('-'));
        if (archive > 0) args[archive] = Path.GetFullPath(args[archive], caller);

        var wasRunning = ServiceActive();
        if (wasRunning && !Environment.IsPrivilegedProcess) return Elevate(args);
        if (wasRunning) Stop(["stop", "--force"]);

        var rc = OwnedByService() ? Run("runuser", ["-u", "baseport", "--", "env", $"DOTNET_BUNDLE_EXTRACT_BASE_DIR={Dir}/.net", Exe, .. args]) : await restore(args);

        if (wasRunning) ServiceCommand("start");
        return rc;
    }

    public static string ServiceUrl(string? unit)
    {
        var url = unit is null ? null : UrlsArgument().Match(unit) is { Success: true } m ? m.Groups[1].Value : null;
        if (string.IsNullOrEmpty(url)) return DefaultUrl;
        return AnyHost().Replace(url.Split(';')[0], "//localhost");
    }

    public static string UnitFile(string dir, string arguments) => $"""
        [Unit]
        Description=Baseport
        After=network.target

        [Service]
        User=baseport
        WorkingDirectory={dir}
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={dir}/.net
        ExecStart={dir}/Baseport {arguments}
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=multi-user.target

        """;

    public static int? LineCount(string[] args) =>
        args.Length < 2 ? 200 : int.TryParse(args[1], System.Globalization.NumberStyles.None, null, out var n) ? n : null;

    [GeneratedRegex(@"--urls[= ]*([^ \n]*)")]
    private static partial Regex UrlsArgument();

    [GeneratedRegex(@"//(0\.0\.0\.0|\[::\]|\*|\+)")]
    private static partial Regex AnyHost();

    private static int Status()
    {
        if (Linux && HasService()) return Run("systemctl", "status", "baseport", "--no-pager", "-n", "10");
        if (WindowsState() is { } state)
        {
            Console.WriteLine($"The Baseport service is {state.ToLowerInvariant()}, on {Url()}.");
            return 0;
        }

        var running = Running();
        Console.WriteLine(running.Count > 0
            ? $"Baseport is running as pid {string.Join(", ", running.Select(p => p.Id))}, on {Url()}."
            : "Baseport is not running. Start it with: baseport");
        if (running.Count == 0) Console.WriteLine($"Or install it as a service: {ServiceHint()}");
        return 0;
    }

    private static int Control(string[] args)
    {
        if (NeedService() is { } problem) return Fail(problem);
        if (!Environment.IsPrivilegedProcess) return Elevate(args);
        ServiceCommand(args[0]);
        if (!ServiceActive())
        {
            if (Linux) Run("systemctl", "status", "baseport", "--no-pager", "-n", "15");
            return Fail($"The Baseport service did not {args[0]}. Look at: {LogHint()}");
        }
        Console.WriteLine($"The Baseport service {args[0]}ed on {Url()}.");
        return 0;
    }

    private static int Stop(string[] args)
    {
        var force = args.Contains("--force") || args.Contains("-Force");
        var stopped = false;

        if (!force)
        {
            if (NeedService() is { } problem) return Fail(problem);
            if (!Environment.IsPrivilegedProcess) return Elevate(args);
            ServiceCommand("stop");
            Console.WriteLine("The Baseport service stopped. It comes back on the next boot until you run: baseport uninstall");
            return 0;
        }

        if (ServiceActive())
        {
            if (!Environment.IsPrivilegedProcess) return Elevate(args);
            stopped = ServiceCommand("stop");
        }

        foreach (var process in Running())
        {
            if (Linux) Run("kill", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else process.Kill();
            if (!process.WaitForExit(10_000)) process.Kill();
            stopped = true;
        }

        if (stopped) Console.WriteLine($"Stopped Baseport in {Dir}.");
        else Console.WriteLine($"Nothing to stop, Baseport in {Dir} is not running.");
        return 0;
    }

    private static int Service(string[] args)
    {
        if (!Environment.IsPrivilegedProcess) return Elevate(args);
        var arguments = args.Length > 1 ? string.Join(' ', args[1..]) : $"--urls {DefaultUrl}";
        if (OperatingSystem.IsWindows()) return WindowsService(arguments);
        if (Which("systemctl") is null) return Fail("No systemd on this machine.");

        if (Run("id", "baseport") != 0)
            Run("useradd", "--system", "--home", Dir, "--shell", File.Exists("/usr/sbin/nologin") ? "/usr/sbin/nologin" : "/sbin/nologin", "baseport");
        Run("chown", "-R", "baseport:baseport", Dir);
        if (Run("su", "-s", "/bin/sh", "baseport", "-c", $"test -r '{Exe}'") != 0)
            return Fail($"The baseport user cannot read {Dir}, the service would not start. Reinstall as root, which lands in /opt/baseport, and try again.");

        File.WriteAllText(Unit, UnitFile(Dir, arguments));
        Run("systemctl", "daemon-reload");
        Run("systemctl", "enable", "baseport");
        Run("systemctl", "restart", "baseport");
        Thread.Sleep(1000);
        if (!ServiceActive())
        {
            Run("systemctl", "status", "baseport", "--no-pager", "-n", "15");
            return Fail("baseport.service was written but is not running.");
        }
        Console.WriteLine($"baseport.service running {Exe} {arguments}");
        return 0;
    }

    private static async Task<int> LogsAsync(string[] args)
    {
        if (LineCount(args) is not { } count) return Fail("The line count has to be a number: baseport logs 50");
        var files = LogFiles();
        var journal = files.Count == 0 && Linux && File.Exists(Unit);
        if (files.Count == 0 && !journal) return Fail($"No log files in {Path.Combine(Dir, "log")} yet.");

        var seeded = journal
            ? Capture("journalctl", "-u", "baseport", "--no-pager").Out.Split('\n').LastOrDefault(l => l.Contains(SeedLine, StringComparison.Ordinal))
            : files.SelectMany(f => ReadShared(f).Split('\n')).LastOrDefault(l => l.Contains(SeedLine, StringComparison.Ordinal));
        if (seeded is not null)
        {
            Console.WriteLine($"First-start login, from {(journal ? "the journal" : Path.Combine(Dir, "log"))}:");
            Console.WriteLine($"  {seeded.Trim()}");
            Console.WriteLine("This is a one-time password. You are asked to change it at first sign-in, so if it no longer works somebody has already used it.");
            Console.WriteLine();
        }

        if (journal) return Run("journalctl", "-u", "baseport", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f");

        string? current = null;
        long position = 0;
        while (true)
        {
            var latest = LogFiles().LastOrDefault() ?? current!;
            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (latest != current || stream.Length < position)
            {
                var tail = current is null ? count : int.MaxValue;
                current = latest;
                position = 0;
                var lines = ReadShared(latest).TrimEnd('\n').Split('\n');
                Console.WriteLine(string.Join('\n', lines.TakeLast(tail)));
                position = stream.Length;
            }
            else if (stream.Length > position)
            {
                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                Console.Write(await reader.ReadToEndAsync());
                position = stream.Length;
            }
            await Task.Delay(500);
        }
    }

    private static async Task<int> DoctorAsync()
    {
        var rc = 0;
        void Ok(string m) => Console.WriteLine($"ok    {m}");
        void Warn(string m) => Console.WriteLine($"warn  {m}");
        void Bad(string m) { Console.WriteLine($"FAIL  {m}"); rc = 1; }

        Ok($"Baseport {CliHelp.Version} in {Dir}");

        var found = Which(OperatingSystem.IsWindows() ? "baseport.cmd" : "baseport");
        if (found is null) Warn($"the baseport command is not on your PATH.");
        else if (Command is { } self && !SamePath(found, self)) Warn($"PATH finds {found} before {self}, so you are running another install.");
        else Ok($"the baseport command is {found}");

        if (Writable(Dir)) Ok($"{Dir} is writable by {Environment.UserName}");
        else Warn($"{Dir} is not writable by {Environment.UserName}, which is normal when the service owns it.");

        var settings = ConfigCli.Build(Path.Combine(Dir, "appsettings.json"), Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));
        var connection = settings["Baseport:ConnectionString"] ?? "Data Source=baseport.db";
        var database = Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection).DataSource);
        if (File.Exists(database))
        {
            Ok($"database {database}, {BackupStore.Human(new FileInfo(database).Length)}");
            var check = new StringWriter();
            if (await DatabaseIntegrity.RunCliAsync(connection, check) == 0) Ok($"database integrity: {check.ToString().Trim()}");
            else Bad($"database integrity: {check.ToString().Trim()}. Restore the latest backup: baseport restore <archive>");
        }
        else Warn("no database yet, the first start creates one and prints a one-time admin login.");

        var unit = Linux && File.Exists(Unit) ? File.ReadAllText(Unit) + "\n" + Capture("systemctl", "show", "baseport", "-p", "Environment", "--value").Out : "";
        if (string.Equals(settings["Baseport:AllowInsecureSignIn"], "true", StringComparison.OrdinalIgnoreCase)
            || InsecureInUnit().IsMatch(unit))
            Bad("Baseport:AllowInsecureSignIn is on: sign-in works over plain HTTP. Turn it off before exposing this instance.");
        if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING") is not null || unit.Contains("DOTNET_SYSTEM_IO_DISABLEFILELOCKING=", StringComparison.Ordinal))
            Warn("DOTNET_SYSTEM_IO_DISABLEFILELOCKING is set: a second Baseport on the same database is not refused.");

        if (HasService())
        {
            if (ServiceActive()) Ok("the Baseport service is running");
            else Bad($"the Baseport service exists but is not running. Look at: {LogHint()}");
        }
        else if (Running().Count > 0) Ok("a Baseport process is running, but there is no service to restart it after a reboot");
        else Warn($"no Baseport service, Baseport only runs while your terminal does. Install one: {ServiceHint()}");

        if (Linux && File.Exists(Unit))
        {
            var runDir = Capture("systemctl", "show", "baseport.service", "-p", "WorkingDirectory", "--value").Out.Trim();
            if (runDir.Length > 0 && runDir != Dir) Warn($"baseport.service runs from {runDir}, not {Dir}, so \"baseport update\" updates a copy nothing runs.");
            if (unit.Contains("--urls", StringComparison.Ordinal) && File.Exists(EnvFile.Path(Dir))
                && EnvFile.Parse(File.ReadAllText(EnvFile.Path(Dir))).Entries.Any(e => e.Name == "BASEPORT_URLS"))
                Warn("BASEPORT_URLS in .env is ignored: baseport.service passes --urls. Set the address with: sudo baseport service --urls URL");
        }

        var report = ConfigCli.Inspect(ConfigCli.Layers(Path.Combine(Dir, "appsettings.json"), Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")), []);
        if (report.Problems.Count == 0) Ok("configuration has no conflicts");
        foreach (var problem in report.Problems) Warn(problem);

        var url = Url();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            (await http.GetAsync(new Uri($"{url}/api/readyz"))).EnsureSuccessStatusCode();
            Ok($"answering on {url}, console at {url}/_/admin");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Warn($"nothing answered on {url}.");
        }

        var drive = new DriveInfo(Path.GetPathRoot(Dir)!);
        Console.WriteLine($"disk  {BackupStore.Human(drive.AvailableFreeSpace)} free on {drive.Name}");
        return rc;
    }

    [GeneratedRegex(@"(Baseport__AllowInsecureSignIn|BASEPORT_ALLOW_INSECURE_SIGNIN)=[""']?true", RegexOptions.IgnoreCase)]
    private static partial Regex InsecureInUnit();

    private static int Uninstall(string[] args)
    {
        var purge = args.Contains("--purge");
        if (!Environment.IsPrivilegedProcess
            && (HasService() || !Writable(Dir) || (Command is { } c && !Writable(Path.GetDirectoryName(c)!))))
            return Elevate(args);

        if (purge && !Console.IsInputRedirected)
        {
            Console.Write($"Delete {Dir} with its database, uploads and backups? This cannot be undone. [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes")) return Fail("Cancelled, nothing was removed.");
        }

        Stop(["stop", "--force"]);
        if (Linux && File.Exists(Unit))
        {
            Run("systemctl", "disable", "--now", "baseport");
            File.Delete(Unit);
            Run("systemctl", "daemon-reload");
            Console.WriteLine("Removed baseport.service.");
        }
        if (!Linux && HasService() && Capture("sc.exe", "delete", ServiceName).Code == 0) Console.WriteLine("Removed the Baseport service.");
        if (!Linux) DropFromUserPath();

        var self = OperatingSystem.IsWindows() ? new[] { Exe, Path.Combine(Dir, "baseport.cmd") } : [];
        foreach (var entry in new DirectoryInfo(Dir).EnumerateFileSystemInfos())
        {
            if ((!purge && Kept.Contains(entry.Name)) || self.Any(s => SamePath(s, entry.FullName))) continue;
            if (entry is DirectoryInfo d) d.Delete(true); else entry.Delete();
        }
        if (purge && Linux)
        {
            Directory.Delete(Dir);
            if (Run("id", "baseport") == 0) Run("userdel", "baseport");
        }

        if (purge) Console.WriteLine($"Removed {Dir} and everything in it.");
        else
        {
            Console.WriteLine($"Removed the Baseport program files from {Dir}.");
            Console.WriteLine("Your data stayed: baseport.db, baseport.key, appsettings.json, .env, uploads, backups, log.");
        }

        if (OperatingSystem.IsWindows())
        {
            var tail = purge ? $"rd /s /q \"{Dir}\"" : string.Join(" & ", self.Select(s => $"del /q \"{s}\""));
            Process.Start(new ProcessStartInfo("cmd", ["/c", $"timeout /t 2 >nul & {tail}"]) { CreateNoWindow = true, UseShellExecute = false });
        }
        else if (Command is { } command) File.Delete(command);
        Console.WriteLine("Removed the baseport command.");
        return 0;
    }

    private static int? AsOwner(string[] args)
    {
        if (!Linux) return null;
        if (OwnedByService())
            return Run("runuser", ["-u", "baseport", "--", "env", $"DOTNET_BUNDLE_EXTRACT_BASE_DIR={Dir}/.net", Exe, .. args]);
        if (Environment.IsPrivilegedProcess) return null;
        var db = Path.Combine(Dir, "baseport.db");
        var blocked = new[] { Dir, db, db + "-wal", db + "-shm" }.Any(p => (File.Exists(p) || Directory.Exists(p)) && !Writable(p))
            || (File.Exists(EnvFile.Path(Dir)) && !Readable(EnvFile.Path(Dir)));
        return blocked ? Elevate(args) : null;
    }

    private static bool OwnedByService() =>
        Linux && Environment.IsPrivilegedProcess && Capture("stat", "-c", "%U", Dir).Out.Trim() == "baseport";

    private static int Elevate(string[] args)
    {
        if (!Linux) return Fail($"That needs an administrator. Open a terminal with Run as administrator and run: baseport {string.Join(' ', args)}");
        if (Which("sudo") is null) return Fail($"That needs root and there is no sudo here. Run it as root: su -c 'baseport {string.Join(' ', args)}'");
        if (Console.IsInputRedirected && Capture("sudo", "-n", "true").Code != 0)
            return Fail($"That needs root, and sudo cannot ask for a password here. Run it as root: sudo baseport {string.Join(' ', args)}");
        Console.Error.WriteLine("This needs root, running it again through sudo.");
        var forwarded = new[] { "BASEPORT_COMMAND", "BASEPORT_CALLER_DIR" }
            .Where(n => Environment.GetEnvironmentVariable(n) is not null)
            .Select(n => $"{n}={Environment.GetEnvironmentVariable(n)}");
        return Run("sudo", ["--", "env", .. forwarded, Exe, .. args]);
    }

    private static string? NeedService() =>
        Linux && Which("systemctl") is null ? "No systemd on this machine, so there is no service to control."
        : !HasService() ? $"There is no Baseport service yet. Create one: {ServiceHint()}"
        : null;

    private static string ServiceHint() => Linux ? "sudo baseport service" : "baseport service, from an elevated terminal";

    private static string LogHint() => Linux ? "journalctl -u baseport -n 50" : Path.Combine(Dir, "log");

    private static bool HasService() =>
        Linux ? File.Exists(Unit) && Which("systemctl") is not null : WindowsState() is not null;

    private static bool ServiceActive() =>
        Linux ? HasService() && Capture("systemctl", "is-active", "--quiet", "baseport").Code == 0 : WindowsState() == "RUNNING";

    private static string? WindowsState() =>
        OperatingSystem.IsWindows() && Capture("sc.exe", "query", ServiceName) is (0, var output) && ScState().Match(output) is { Success: true } m
            ? m.Groups[1].Value
            : null;

    [GeneratedRegex(@"STATE\s*:\s*\d+\s+(\w+)")]
    private static partial Regex ScState();

    private static bool ServiceCommand(string verb)
    {
        if (Linux) return Run("systemctl", verb, "baseport") == 0;
        if (verb is "stop" or "restart")
        {
            Capture("sc.exe", "stop", ServiceName);
            WaitFor("STOPPED");
        }
        if (verb == "stop") return WindowsState() == "STOPPED";
        Capture("sc.exe", "start", ServiceName);
        return WaitFor("RUNNING");
    }

    private static bool WaitFor(string state)
    {
        for (var i = 0; i < 30 && WindowsState() != state; i++) Thread.Sleep(500);
        return WindowsState() == state;
    }

    private static int WindowsService(string arguments)
    {
        var account = new[] { "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "password=", "" };
        var binPath = $"\"{Exe}\" {arguments}";
        if (Run("icacls", Dir, "/grant", $"{LocalService}:(OI)(CI)M", "/T", "/Q") != 0)
            return Fail($"Could not give the LocalService account access to {Dir}.");
        var (code, output) = HasService()
            ? Capture("sc.exe", ["config", ServiceName, "binPath=", binPath, .. account])
            : Capture("sc.exe", ["create", ServiceName, "binPath=", binPath, "DisplayName=", "Baseport", .. account]);
        if (code != 0) return Fail(output.Trim());
        Capture("sc.exe", "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000");
        if (!ServiceCommand("restart")) return Fail($"The Baseport service was created but is not running. Look at: {LogHint()}");
        Console.WriteLine($"The Baseport service is running {binPath} as LocalService, on {Url()}.");
        return 0;
    }

    private static string Url() =>
        ServiceUrl(Linux ? (File.Exists(Unit) ? File.ReadAllText(Unit) : null) : WindowsState() is null ? null : Capture("sc.exe", "qc", ServiceName).Out);

    private static IReadOnlyList<Process> Running() =>
        Process.GetProcessesByName("Baseport")
            .Where(p => p.Id != Environment.ProcessId && PathOf(p) is { } path && SamePath(path, Exe))
            .ToList();

    private static string? PathOf(Process process)
    {
        try
        {
            return Linux ? File.ReadAllText($"/proc/{process.Id}/cmdline").Split('\0')[0] : process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> LogFiles()
    {
        var log = Path.Combine(Dir, "log");
        return Directory.Exists(log)
            ? Directory.GetFiles(log, "baseport-*.log").OrderBy(File.GetLastWriteTimeUtc).ToList()
            : [];
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private static void DropFromUserPath()
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (path is null) return;
        var kept = string.Join(';', path.Split(';').Where(p => p.Length > 0 && !SamePath(p.TrimEnd('\\'), Dir.TrimEnd('\\'))));
        if (kept == path) return;
        Environment.SetEnvironmentVariable("Path", kept, EnvironmentVariableTarget.User);
        Console.WriteLine($"Removed {Dir} from your user PATH.");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Writable(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var probe = Path.Combine(path, $".baseport-probe-{Environment.ProcessId}");
                File.Create(probe, 1, FileOptions.DeleteOnClose).Dispose();
            }
            else new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Readable(string path)
    {
        try
        {
            File.OpenRead(path).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);

    private static int Run(string file, params IEnumerable<string> args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false })!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return 127;
        }
    }

    private static (int Code, string Out) Capture(string file, params IEnumerable<string> args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var stderr = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = stderr.Result;
            return (process.ExitCode, output);
        }
        catch (Win32Exception)
        {
            return (127, "");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
