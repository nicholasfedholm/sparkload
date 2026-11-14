using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SparkLoad.Launch
{
    /// <summary>
    /// `sparkload kit`: portable audit mode for a player's own game. Discovers the game and data folder, launches a
    /// normal game session under the trace, never kills the game, never applies a memory cap, and analyzes after the
    /// player closes the game. It does not copy mods or saves and does not open a world.
    /// </summary>
    internal static class Kit
    {
        const string ConfigName = "portable-kit.json";
        const string DefaultRunPrefix = "kit-";

        sealed class Config
        {
            public string? GameExe;
            public string? DataDir;
            public string? OutputDir;
            public double? MaxTraceMb;
            public string? Source;
        }

        static void Say(string s) { try { Console.Out.WriteLine(s); Console.Out.Flush(); } catch { } }

        static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name) ?? ""; } catch { return ""; }
        }

        static string Expand(string s)
        {
            try { return Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')); } catch { return s.Trim(); }
        }

        static string NormDir(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p.TrimEnd('\\', '/'); }
        }

        /// <summary>Folder that holds sparkload.exe (never the current directory).</summary>
        static string ExeDir() => NormDir(AppContext.BaseDirectory);

        /// <summary>Kit root: the parent of the sparkload folder.</summary>
        static string KitRoot()
        {
            string? p = Path.GetDirectoryName(ExeDir());
            return string.IsNullOrEmpty(p) ? ExeDir() : p;
        }

        static string? ReadString(JsonElement root, string key)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                return s.Trim().Length == 0 ? null : Expand(s);
            }
            return null;
        }

        /// <summary>Loads portable-kit.json (next to the exe, else in the kit root). Returns an error text or null.</summary>
        static string? LoadConfig(string? explicitPath, Config cfg)
        {
            string? path = null;
            if (explicitPath != null) path = explicitPath;
            else
            {
                string a = Path.Combine(ExeDir(), ConfigName);
                string b = Path.Combine(KitRoot(), ConfigName);
                if (File.Exists(a)) path = a; else if (File.Exists(b)) path = b;
            }
            if (path == null) return null;
            if (!File.Exists(path)) return "The settings file was not found: " + path;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return "The settings file must be a JSON object: " + path;
                cfg.GameExe = ReadString(doc.RootElement, "game_exe");
                cfg.DataDir = ReadString(doc.RootElement, "data_dir");
                cfg.OutputDir = ReadString(doc.RootElement, "output_dir");
                if (doc.RootElement.TryGetProperty("max_trace_mb", out var m))
                {
                    double d;
                    if (m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out d) && d >= 0) cfg.MaxTraceMb = d;
                    else if (m.ValueKind == JsonValueKind.String && double.TryParse(m.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0) cfg.MaxTraceMb = d;
                    else if (m.ValueKind != JsonValueKind.Null && !(m.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(m.GetString()))) return "max_trace_mb in " + path + " must be a number (0 = no limit).";
                }
                cfg.Source = path;
                return null;
            }
            catch (Exception ex)
            {
                return "The settings file could not be read (" + path + "): " + ex.Message;
            }
        }

        static IEnumerable<string> GameCandidates()
        {
            string appdata = Env("APPDATA"), pf = Env("ProgramFiles"), pf86 = Env("ProgramFiles(x86)"), local = Env("LOCALAPPDATA");
            if (appdata.Length > 0) yield return Path.Combine(appdata, "Vintagestory", "Vintagestory.exe");
            if (pf.Length > 0) yield return Path.Combine(pf, "Vintagestory", "Vintagestory.exe");
            if (pf86.Length > 0) yield return Path.Combine(pf86, "Vintagestory", "Vintagestory.exe");
            if (local.Length > 0) yield return Path.Combine(local, "Programs", "Vintagestory", "Vintagestory.exe");
            // Optimum last, only reached when no stock install exists.
            if (local.Length > 0) yield return Path.Combine(local, "Programs", "Optimum", "Optimum.exe");
        }

        /// <summary>A game path can be the exe itself or its folder.</summary>
        static string? ResolveGamePath(string p)
        {
            try
            {
                if (File.Exists(p)) return Path.GetFullPath(p);
                if (Directory.Exists(p))
                {
                    foreach (string n in new[] { "Vintagestory.exe", "Optimum.exe" })
                    {
                        string f = Path.Combine(p, n);
                        if (File.Exists(f)) return Path.GetFullPath(f);
                    }
                }
            }
            catch { }
            return null;
        }

        static string? ResolveHook(string? overridePath)
        {
            if (overridePath != null) return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
            foreach (string c in new[] { Path.Combine(ExeDir(), "hook", "SparkLoad.Hook.dll"), Path.Combine(ExeDir(), "SparkLoad.Hook.dll") })
                if (File.Exists(c)) return c;
            return null;
        }

        static string DefaultDataDir()
        {
            string appdata = Env("APPDATA");
            return appdata.Length > 0 ? Path.Combine(appdata, "VintagestoryData") : "";
        }

        static bool SamePath(string a, string b) => string.Equals(NormDir(a), NormDir(b), StringComparison.OrdinalIgnoreCase);

        public static int Run(string[] args)
        {
            bool dryRun = false;
            string? runDirFile = null;
            string guardNames = "Vintagestory,Optimum";
            string? game = null, data = null, outDir = null, cfgPath = null, runId = null, hookOverride = null;
            double? tail = null, maxSec = null, maxTrace = null;
            var gameArgs = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--") { for (int j = i + 1; j < args.Length; j++) gameArgs.Add(args[j]); break; }
                string? v;
                switch (a)
                {
                    case "--dry-run": dryRun = true; break;
                    case "--run-dir-file": if ((v = NextArg(args, ref i)) == null) return Bad("--run-dir-file needs a value"); runDirFile = Expand(v); break;
                    case "--guard-names": if ((v = NextArg(args, ref i)) == null) return Bad("--guard-names needs a value"); guardNames = v; break;
                    case "--game": if ((v = NextArg(args, ref i)) == null) return Bad("--game needs a value"); game = Expand(v); break;
                    case "--data": if ((v = NextArg(args, ref i)) == null) return Bad("--data needs a value"); data = Expand(v); break;
                    case "--out": if ((v = NextArg(args, ref i)) == null) return Bad("--out needs a value"); outDir = Expand(v); break;
                    case "--config": if ((v = NextArg(args, ref i)) == null) return Bad("--config needs a value"); cfgPath = Expand(v); break;
                    case "--run-id": if ((v = NextArg(args, ref i)) == null) return Bad("--run-id needs a value"); runId = v; break;
                    case "--hook": if ((v = NextArg(args, ref i)) == null) return Bad("--hook needs a value"); hookOverride = Expand(v); break;
                    case "--tail-seconds":
                    case "--max-seconds":
                    case "--max-trace-mb":
                        v = NextArg(args, ref i);
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < 0 || (a == "--max-seconds" && d <= 0)) return Bad(a + " needs a number");
                        if (a == "--tail-seconds") tail = d; else if (a == "--max-seconds") maxSec = d; else maxTrace = d;
                        break;
                    default:
                        return Bad("unknown option " + a);
                }
            }

            var cfg = new Config();
            string? cerr = LoadConfig(cfgPath, cfg);
            if (cerr != null)
            {
                Say("Problem: " + cerr);
                Say("Fix or delete " + ConfigName + " and run again.");
                return 2;
            }
            if (runDirFile != null)
            {
                // Writes one line, the folder this run will use, into the given file. RUN-AUDIT.cmd reads it (a GUI-subsystem
                // exe cannot be captured through a cmd pipe, so a file is used).
                string pbase = NormDir(outDir ?? cfg.OutputDir ?? Path.Combine(KitRoot(), "output"));
                string prid = runId ?? DefaultRunPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.WriteAllText(runDirFile, Path.Combine(pbase, prid), new System.Text.UTF8Encoding(false));
                return 0;
            }
            Say("SparkLoad audit kit " + Launcher.Version);
            Say("settings file: " + (cfg.Source ?? "none (everything is found automatically)"));

            // Game: --game, then portable-kit.json, then the standard install places (Optimum last).
            string? gameExe = null;
            string how = "";
            string? wanted = game ?? cfg.GameExe;
            if (wanted != null)
            {
                gameExe = ResolveGamePath(wanted);
                how = game != null ? "from --game" : "from game_exe in " + ConfigName;
                if (gameExe == null)
                {
                    Say("Problem: the game was not found at the place you set: " + wanted);
                    Say("Open " + ConfigName + " in Notepad (it is in the sparkload folder) and set game_exe to the full path of Vintagestory.exe, for example C:\\Users\\YourName\\AppData\\Roaming\\Vintagestory\\Vintagestory.exe");
                    return 2;
                }
            }
            else
            {
                foreach (string c in GameCandidates())
                {
                    if (File.Exists(c)) { gameExe = c; how = "found automatically"; break; }
                }
                if (gameExe == null)
                {
                    Say("Problem: Vintage Story was not found in the usual places.");
                    Say("Open " + ConfigName + " in Notepad (it is in the sparkload folder), set game_exe to the full path of Vintagestory.exe, save it, and run again.");
                    Say("Example: \"game_exe\": \"C:\\\\Users\\\\YourName\\\\AppData\\\\Roaming\\\\Vintagestory\\\\Vintagestory.exe\"");
                    return 2;
                }
            }

            // Data folder: --data, then data_dir in the settings file, then the standard %APPDATA%\VintagestoryData.
            string defData = DefaultDataDir();
            string? dataDir = data ?? cfg.DataDir;
            string dataHow;
            if (dataDir != null) dataHow = data != null ? "from --data" : "from data_dir in " + ConfigName;
            else if (defData.Length > 0 && Directory.Exists(defData)) { dataDir = defData; dataHow = "found automatically"; }
            else { dataHow = "not found"; }
            bool nonDefault = dataDir != null && (defData.Length == 0 || !SamePath(dataDir, defData));
            if (dataDir != null) dataDir = NormDir(dataDir);

            string? hook = ResolveHook(hookOverride);
            string root = KitRoot();
            string outBase = NormDir(outDir ?? cfg.OutputDir ?? Path.Combine(root, "output"));
            string rid = runId ?? DefaultRunPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            double traceMb = maxTrace ?? cfg.MaxTraceMb ?? 1536;

            Say("game:   " + gameExe + "  (" + how + ")");
            Say("data:   " + (dataDir ?? "not found") + "  (" + dataHow + ")");
            Say("hook:   " + (hook ?? "MISSING"));
            Say("output: " + Path.Combine(outBase, rid));
            Say("game data path argument: " + (nonDefault ? "--dataPath " + dataDir + " (not the default data folder)" : "none (normal launch)"));
            Say("trace size limit: " + (traceMb > 0 ? traceMb.ToString("0.##", CultureInfo.InvariantCulture) + " MB" : "none") + "; the game is never closed or limited by this tool");
            if (dataDir == null)
                Say("Note: the data folder was not found, so the end of loading cannot be detected from the game log. The trace will stop after " + (maxSec ?? 900).ToString("0", CultureInfo.InvariantCulture) + " seconds instead.");
            else if (!Directory.Exists(dataDir))
                Say("Note: the data folder does not exist yet: " + dataDir);

            if (hook == null)
            {
                Say("Problem: SparkLoad.Hook.dll is missing from the sparkload folder. Unzip the whole kit again.");
                return 2;
            }

            var running = Guard.Scan(RunOptions.SplitGuard(guardNames));
            if (dryRun)
            {
                if (running.Count > 0) Say("note: Vintage Story is running right now; a real run would stop with 'Please close Vintage Story first'");
                Say("dry-run: nothing was launched, no folder was created");
                return 0;
            }
            if (running.Count > 0)
            {
                Say("Please close Vintage Story first, then run again");
                return 3;
            }

            var o = new RunOptions
            {
                Exe = gameExe,
                Out = outBase,
                RunId = rid,
                Mode = "default",
                Then = "wait",
                GuardNames = guardNames,
                Data = dataDir,
                Hook = hook,
                Kit = true,
                MaxChildGb = 0,
                MaxTraceMb = traceMb,
            };
            if (tail.HasValue) o.TailSeconds = tail.Value;
            if (maxSec.HasValue) o.MaxSeconds = maxSec.Value;
            if (nonDefault) { o.GameArgs.Add("--dataPath"); o.GameArgs.Add(dataDir!); }
            o.GameArgs.AddRange(gameArgs);

            using var log = new RunLog();
            int rc = Launcher.Run(o, log);
            string rdir = Path.Combine(outBase, rid);
            if (rc == 0 || rc == 5 || rc == 6)
                log.Line("kit: results are in " + rdir + (File.Exists(Path.Combine(rdir, "loading.md")) ? " (loading.md, loading.csv)" : ""));
            return rc;
        }

        static string? NextArg(string[] args, ref int i)
        {
            return i + 1 < args.Length ? args[++i] : null;
        }

        static int Bad(string s)
        {
            try { Console.Error.WriteLine("sparkload kit: " + s); } catch { }
            return 2;
        }
    }
}
