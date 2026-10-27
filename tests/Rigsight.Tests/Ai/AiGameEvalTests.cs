using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Ai;

/// <summary>
/// RigSight AI asked about games on made-up PCs whose answer is known (the person's ask, 2026-10-25: "have we run AI
/// simulations of the game settings and the FPS capture?"). Each scenario is a PC lived by <see cref="PcSim"/> for ten
/// days, with every play's frames (lows, stutters), what else was busy, and the game's own settings file laid out where
/// the game keeps it. The questions are what people ask; each answer is checked by rule where a rule can say it (the
/// right look-ups, a web search for this PC's parts, every frame rate it quotes really in the records, the setting that
/// holds the game back named) and written out in full, with what the AI was shown, for a person to read.
/// Runs only when RIGSIGHT_LIVE_RELAY names a local relay started with ADMIN_DEV=1 (it gives this test PC Complete);
/// EVAL_REPEAT asks each question that many times, since answers vary. Writes screens\game-eval.md.
/// </summary>
public sealed class AiGameEvalTests
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_RELAY");
    private static readonly string Out = Path.Combine(TestEnvironment.DataDir, "screens", "game-eval.md");

    /// <summary>One made-up PC: which game, its frames, what else ran, and its settings file.</summary>
    private sealed record Scenario(string Id, string Story, int Game, Profile Frames, Action<GameSettings.Places>? Settings,
        (string Id, string Text, Check Check)[] Questions, string RivaTuner = "running");

    /// <summary>How a game's frames go: its usual rate and spread, stutters an hour, and what changes in the last play's rough patch.</summary>
    private sealed record Profile(double Fps, double Jitter, double StuttersAnHour, double? GpuLoad = null, double? Cap = null,
        string? BusyApp = null, double BusyDiskMBs = 0, double VramFullGb = 0, double RoughStuttersAnHour = 0, bool NoFrames = false);

    /// <summary>What a good answer does. Each is a rule a script can check; the rest is read by a person.</summary>
    private sealed record Check(bool Search = false, string[]? Names = null, string[]? NotNames = null, bool NoGpuAdvice = false, string? Note = null);

    private static readonly double ScreenHz = GameSettings.ScreenHz() ?? 60;

    private static Scenario[] Scenarios() =>
    [
        new("gpu-bound", "Cyberpunk 2077 at 1440p with path tracing on and no upscaler: the graphics card at 98-99%, about 38 fps, lows near 27.", 0,
            new Profile(Fps: 38, Jitter: 0.12, StuttersAnHour: 6, GpuLoad: 99),
            p => Cyberpunk(p, s =>
            {
                s["/graphics/presets"]["ResolutionScaling"] = "Off";
                s["/graphics/presets"]["DLSS"] = "Off";
                s["/graphics/raytracing"]["RayTracedPathTracing"] = true;
            }),
            [
                ("more-fps", "How can I get more FPS in Cyberpunk?", new Check(Search: true, Names: ["DLSS", "path tracing|Path Tracing"],
                    Note: "GPU-bound: turn on DLSS (Quality/Balanced) and/or path tracing off; says the card is the limit; numbers from records")),
                ("best", "What are the best settings for Cyberpunk 2077 on my PC?", new Check(Search: true, Names: ["DLSS"],
                    Note: "searches for this card (RTX 3080 Ti) at 1440p; tailored list, not a generic preset dump")),
                ("good", "Is my FPS good in Cyberpunk?", new Check(Note: "≈38 fps avg, lows ≈27: playable but low for this card with path tracing; quotes lows")),
                ("wrong", "Cyberpunk only runs at 15 fps on my PC, why?", new Check(Note: "corrects: about 38 fps on record, never 15")),
            ]),
        new("capped", "Dota 2 with VSync on: a flat 60 fps while the graphics card idles at about 40%.", 1,
            new Profile(Fps: 60, Jitter: 0.02, StuttersAnHour: 2, GpuLoad: 40, Cap: 60),
            p => Dota(p, vsync: true),
            [
                ("stuck60", "Why is Dota 2 stuck at 60 fps?", new Check(Names: ["VSync|V-Sync|vsync"], NoGpuAdvice: true,
                    Note: $"VSync on caps it; screen is {ScreenHz} Hz; card at 40% so it isn't the card; no 'lower shadows'")),
                ("smoother", "How do I make Dota 2 smoother?", new Check(Names: ["VSync|V-Sync|vsync"], NoGpuAdvice: true,
                    Note: "VSync/limit vs screen Hz first; doesn't invent stutters")),
            ]),
        new("disk", "Hades, settings not readable; in the last play OneDrive read ~140 MB/s for a stretch and stutters bunched there.", 2,
            new Profile(Fps: 144, Jitter: 0.05, StuttersAnHour: 3, BusyApp: "OneDrive", BusyDiskMBs: 140, RoughStuttersAnHour: 60),
            null,
            [
                ("stutter", "Why does Hades stutter?", new Check(Names: ["OneDrive"],
                    Note: "names OneDrive reading the disk during the rough stretch, with its time; not generic shader-cache tips first")),
                ("better", "How do I make Hades run better?", new Check(Note: "says its settings couldn't be read; already fast (≈144 fps); OneDrive stutters; general tips only as general")),
            ]),
        new("vram", "Cyberpunk 2077 with path tracing, frame generation and high textures: graphics card memory full (11.9 of 12 GB) and stutters in the last hour.", 0,
            new Profile(Fps: 70, Jitter: 0.1, StuttersAnHour: 5, GpuLoad: 97, VramFullGb: 11.9, RoughStuttersAnHour: 50),
            p => Cyberpunk(p, s =>
            {
                s["/graphics/raytracing"]["RayTracedPathTracing"] = true;
                s["/graphics/presets"]["DLSSFrameGen"] = true;
                s["/graphics/presets"]["FrameGeneration"] = "DLSS";
            }),
            [
                ("late-stutter", "Why does Cyberpunk start stuttering after I've played a while?", new Check(Names: ["memory|VRAM"], Search: false,
                    Note: "graphics card memory full in that stretch; lower Texture Quality / path tracing / frame gen")),
            ]),
        new("no-rivatuner", "Dota 2 played without RivaTuner installed: no frame rates at all; settings readable (VSync off, 1440p).", 1,
            new Profile(Fps: 0, Jitter: 0, StuttersAnHour: 0, GpuLoad: 55, NoFrames: true),
            p => Dota(p, vsync: false),
            [
                ("what-fps", "What FPS do I get in Dota 2?", new Check(Names: ["RivaTuner"],
                    Note: "no frame rate on record because RivaTuner isn't installed; how to get it (install it from the Overlay page); never a guessed number")),
                ("more-fps", "How can I get more FPS in Dota 2?", new Check(Names: ["RivaTuner"],
                    Note: "still advises from its settings and load (card at 55%), says it can't see the frame rate without RivaTuner and how to add it")),
            ], RivaTuner: "missing"),
    ];

    // ── The settings files, laid out as the real games keep them ──

    /// <summary>Cyberpunk 2077's UserSettings.json (its real layout), changed per scenario.</summary>
    private static void Cyberpunk(GameSettings.Places p, Action<Dictionary<string, Dictionary<string, object>>> change)
    {
        var s = new Dictionary<string, Dictionary<string, object>>
        {
            ["/graphics/presets"] = new() { ["QuickPresets"] = "Custom", ["ResolutionScaling"] = "DLSS", ["DLSS"] = "Quality", ["FSR2"] = "Quality",
                ["DynamicResolutionScaling"] = false, ["FrameGeneration"] = "Off", ["DLSSFrameGen"] = false, ["TextureQuality"] = "High" },
            ["/graphics/raytracing"] = new() { ["RayTracing"] = true, ["RayTracedReflections"] = true, ["RayTracedSunShadows"] = true,
                ["RayTracedLocalShadows"] = true, ["RayTracedLighting"] = "Psycho", ["RayTracedPathTracing"] = false },
            ["/video/display"] = new() { ["VSync"] = "UI-Settings-Video-QualitySetting-Off", ["MaximumFPS_OnOff"] = false, ["MaximumFPS_Value"] = 30,
                ["WindowMode"] = "BorderlessWindowed", ["Resolution"] = "2560x1440", ["ReflexMode"] = "Enabled" },
            ["/graphics/advanced"] = new() { ["ShadowMeshQuality"] = "High", ["LocalShadowsQuality"] = "High", ["CascadedShadowsResolution"] = "High",
                ["VolumetricFogResolution"] = "Ultra", ["VolumetricCloudsQuality"] = "Ultra", ["ScreenSpaceReflectionsQuality"] = "Insane",
                ["AmbientOcclusion"] = "High", ["LODPreset"] = "High" },
            ["/graphics/performance"] = new() { ["CrowdDensity"] = "High" },
        };
        change(s);
        var json = new JsonObject
        {
            ["version"] = 3,
            ["data"] = new JsonArray([.. s.Select(g => (JsonNode)new JsonObject
            {
                ["group_name"] = g.Key,
                ["options"] = new JsonArray([.. g.Value.Select(o => (JsonNode)new JsonObject { ["name"] = o.Key, ["type"] = "x", ["value"] = o.Value switch { bool b => JsonValue.Create(b), int i => JsonValue.Create(i), _ => JsonValue.Create(o.Value.ToString()) } })]),
            })]),
        };
        Write(Path.Combine(p.LocalAppData, "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json"), json.ToJsonString());
    }

    /// <summary>Dota 2's video.txt (on a real PC in Steam's userdata, found by its Steam id; a made-up PC has no Steam install, so a folder named after it).</summary>
    private static void Dota(GameSettings.Places p, bool vsync) =>
        Write(Path.Combine(p.LocalAppData, "Dota 2", "cfg", "video.txt"), $$"""
            "video.cfg"
            {
            	"setting.defaultres"		"2560"
            	"setting.defaultresheight"		"1440"
            	"setting.fullscreen"		"1"
            	"setting.nowindowborder"		"0"
            	"setting.mat_vsync"		"{{(vsync ? 1 : 0)}}"
            	"setting.r_dota_fsr_upsample"		"0"
            	"setting.r_texture_stream_mip_bias"		"0"
            	"setting.r_dashboard_render_quality"		"1"
            }
            """);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ── The PC ──

    /// <summary>Ten days of the scenario's game, then every play's frames written as the agent writes them.</summary>
    private static (string Db, GameSettings.Places Places, string Truth) Build(Scenario s)
    {
        var root = TestEnvironment.NewFolder("game-eval-" + s.Id);
        string db = Path.Combine(root, "rigsight.db");
        var today = DateTime.Today;
        PcSim.Live(db, today.AddDays(-10), DateTime.Now.AddHours(-1), new PcSim.Options { Seed = 7, Game = s.Game, GameChance = 1, GameHours = 2.5 });

        var game = PcSim.Games[s.Game];
        var f = s.Frames;
        var rnd = new Random(11);
        var truth = new StringBuilder();
        using (var w = RigsightDb.OpenWriter(db))
        using (var raw = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            long app = Scalar(raw, $"SELECT id FROM apps WHERE exe = '{game.Exe}'");
            var plays = new List<(long Start, long End)>();
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = $"SELECT start, end FROM sessions WHERE app_id = {app} ORDER BY start";
                using var r = cmd.ExecuteReader();
                while (r.Read()) plays.Add((r.GetInt64(0), r.GetInt64(1)));
            }
            long busy = f.BusyApp is { } b ? w.UpsertApp(b.ToLowerInvariant() + ".exe", b, null, AppCategory.Other) : 0;
            var last = plays[^1];
            // The rough stretch: 15 minutes, a third of the way into the newest play.
            long roughFrom = last.Start + (last.End - last.Start) / 3 / 60 * 60, roughTo = roughFrom + 15 * 60;
            var sql = new List<string>();
            foreach (var (start, end) in plays)
                for (long ts = start; ts < end; ts += 60)
                {
                    bool rough = ts >= roughFrom && ts < roughTo;
                    double perHour = rough && f.RoughStuttersAnHour > 0 ? f.RoughStuttersAnHour : f.StuttersAnHour;
                    var spread = new FrameSpread();
                    var stutters = new List<Stutter>();
                    double fps = f.Fps * (0.95 + rnd.NextDouble() * 0.1);
                    long at = ts * 1000;
                    for (int i = 0; i < (f.NoFrames ? 0 : 60 * fps); i++)
                    {
                        double ms = 1000 / fps * (1 + (rnd.NextDouble() - 0.5) * 2 * f.Jitter);
                        if (f.Cap is { } cap) ms = Math.Max(ms, 1000 / cap * (1 + (rnd.NextDouble() - 0.5) * 0.02));
                        // A few frames a minute in the slow tail (what makes a 1% low lower than the average).
                        if (rnd.NextDouble() < 0.012) ms *= 1.35 + rnd.NextDouble() * 0.25;
                        if (rnd.NextDouble() < perHour / 3600 / fps)
                        {
                            double hitch = Math.Max(20, ms * (3 + rnd.NextDouble() * 4));
                            stutters.Add(new Stutter(at, hitch, 1000 / fps));
                            ms = hitch;
                        }
                        spread.Add((uint)(ms * 1000));
                        at += (long)ms;
                    }
                    if (!f.NoFrames) w.WriteFrameMinute(ts, app, spread, stutters);
                    if (f.GpuLoad is { } load)
                        sql.Add($"UPDATE system_minute SET gpu_load = {load - 1 + rnd.NextDouble() * 2:0.0} WHERE ts = {ts}");
                    double vram = rough && f.VramFullGb > 0 ? f.VramFullGb : 7.5 + rnd.NextDouble();
                    sql.Add($"UPDATE system_minute SET vram_used = {vram * 1024:0}, vram_total = {12 * 1024} WHERE ts = {ts}");
                    sql.Add($"INSERT OR REPLACE INTO app_minute(ts, app, cpu, gpu, mem, disk) VALUES({ts}, {app}, {game.CpuLoad}, {f.GpuLoad ?? game.GpuLoad}, {6L << 30}, {2L << 20})");
                    if (busy > 0)
                        sql.Add($"INSERT OR REPLACE INTO app_minute(ts, app, cpu, gpu, mem, disk) VALUES({ts}, {busy}, 3, 0, {300L << 20}, {(long)((rough ? f.BusyDiskMBs : 0.4) * 1e6)})");
                }
            using (var tx = raw.BeginTransaction())
            {
                foreach (var q in sql) Exec(raw, tx, q);
                tx.Commit();
            }
            truth.Append($"{plays.Count} plays of {game.Name}; rough stretch {TimeUtil.FromUnix(roughFrom):ddd h:mm tt}-{TimeUtil.FromUnix(roughTo):h:mm tt}");
        }

        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        var places = new GameSettings.Places
        {
            LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), LocalLow = Dir("LocalLow"), Documents = Dir("Documents"),
            SavedGames = Dir("Saved Games"), Steam = Dir("Steam"),
        };
        s.Settings?.Invoke(places);
        return (db, places, truth.ToString());
    }

    private static long Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    // ── The checks a script can make ──

    /// <summary>Every "N fps" in the answer, against the numbers in what the AI was shown (within 1).</summary>
    private static List<string> MadeUpFps(string answer, string shown)
    {
        var known = Regex.Matches(shown, @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        // A line the answer credits to a web source ("[src: w1]") quotes a guide or benchmark, not this PC.
        answer = string.Join("\n", answer.Split('\n').Where(l => !Regex.IsMatch(l, @"\[src:[^\]]*\bw\d")));
        return [.. Regex.Matches(answer, @"(\d+(?:\.\d+)?)\s*(?:fps|FPS|frames a second|frames per second)")
            .Select(m => m.Groups[1].Value)
            .Where(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) is var n && !known.Any(k => Math.Abs(k - n) <= 1))
            .Distinct()];
    }

    private static readonly Regex GpuAdvice = new(@"\b(lower|reduce|turn down|drop)\b[^.\n]{0,40}\b(shadows?|textures?|resolution|ray tracing|anti-?aliasing|quality)\b", RegexOptions.IgnoreCase);

    private static async Task<JsonNode?> Admin(string path, object body)
    {
        using var http = new HttpClient { BaseAddress = new Uri(RelayUrl!.TrimEnd('/') + "/") };
        var res = await http.PostAsJsonAsync($"admin/api/{path}", body);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"admin {path}: {(int)res.StatusCode} {text}");
        return JsonNode.Parse(text);
    }

    [Fact]
    public async Task Game_questions_on_made_up_PCs_are_answered_from_their_records()
    {
        // EVAL_DRY=1: only build the PCs and write what get_game shows (no relay, no questions).
        bool dry = Environment.GetEnvironmentVariable("EVAL_DRY") == "1";
        if (!dry && (RelayUrl is null || RelayUrl.Contains("rigsight.app"))) return;
        if (!dry) Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", RelayUrl);
        var useActions2 = Relay.Features.UseActions2;
        Relay.Features.UseActions2 = true;
        try
        {
            int repeat = int.TryParse(Environment.GetEnvironmentVariable("EVAL_REPEAT"), out int r) && r > 1 ? r : 1;
            var only = Environment.GetEnvironmentVariable("EVAL_ONLY")?.Split(',').ToHashSet();
            // This test PC on Complete with room for every question (the admin API of wrangler dev).
            if (!dry)
            {
                var (status, problem) = await Relay.StatusAsync();
                Assert.True(status is not null, problem);
                await Admin($"devices/{Relay.DeviceId}", new { action = "give_plan", plan = "complete", reason = "game eval" });
                await Admin($"devices/{Relay.DeviceId}", new { action = "cap_override", n = 500 });
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# RigSight AI on games, made-up PCs, {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Relay {RelayUrl}, app {Relay.Version}, prompt actions-2, each question {repeat}×. Screen here {ScreenHz} Hz.");
            sb.AppendLine();
            var score = new List<(string Id, bool Pass, string Why)>();
            Directory.CreateDirectory(Path.GetDirectoryName(Out)!);
            int asked = 0;
            foreach (var s in Scenarios())
            {
                if (only is not null && !only.Contains(s.Id)) continue;
                var (db, places, truth) = Build(s);
                var settings = new RigsightSettings();
                var tools = new AiTools(settings, db) { SettingsPlaces = places, RivaTuner = () => s.RivaTuner };
                string shown = tools.Run("get_game", new JsonObject { ["name"] = PcSim.Games[s.Game].Name }.ToJsonString());
                sb.AppendLine($"## Scenario {s.Id}");
                sb.AppendLine(s.Story + " " + truth + ".");
                sb.AppendLine();
                sb.AppendLine("<details><summary>What get_game shows the AI</summary>\n\n```text\n" + shown.Trim() + "\n```\n</details>");
                sb.AppendLine();
                File.WriteAllText(Out, sb.ToString());
                if (dry) continue;
                foreach (var (qid, text, check) in s.Questions)
                    for (int i = 1; i <= repeat; i++)
                    {
                        if (asked++ > 0) await Task.Delay(TimeSpan.FromSeconds(8));
                        var fresh = new AiTools(settings, db) { SettingsPlaces = places, RivaTuner = () => s.RivaTuner };
                        var started = DateTime.Now;
                        AiReply reply;
                        for (int attempt = 0; ; attempt++)
                        {
                            try { reply = await Relay.AskAsync([], text, fresh); }
                            catch (Exception ex) { reply = new AiReply(null, ex.GetType().Name + ": " + ex.Message, []); }
                            if (attempt >= 2 || reply.Problem?.Contains("a lot of questions") != true) break;
                            await Task.Delay(TimeSpan.FromSeconds(65));
                        }
                        string answer = reply.Text ?? "";
                        var fails = new List<string>();
                        if (reply.Problem is { } p) fails.Add("problem: " + p);
                        if (!reply.LookUps.Any(l => l.Tool == "get_game")) fails.Add("no get_game");
                        var searches = reply.LookUps.Where(l => l.Tool == "search_web").Select(l => (string?)l.Args["query"] ?? "").ToList();
                        if (check.Search && searches.Count == 0) fails.Add("no web search");
                        foreach (var name in check.Names ?? []) if (!Regex.IsMatch(answer, name, RegexOptions.IgnoreCase)) fails.Add($"doesn't name {name}");
                        if (check.NoGpuAdvice && GpuAdvice.Match(answer) is { Success: true } m) fails.Add($"GPU advice for a capped game: \"{m.Value}\"");
                        var madeUp = MadeUpFps(answer, shown + "\n" + text);
                        if (madeUp.Count > 0) fails.Add("fps not in the records: " + string.Join(", ", madeUp));
                        string id = $"{s.Id}/{qid}" + (repeat > 1 ? $"#{i}" : "");
                        score.Add((id, fails.Count == 0, string.Join("; ", fails)));
                        sb.AppendLine($"### {id}: {text}");
                        sb.AppendLine($"*Good answer:* {check.Note}");
                        sb.AppendLine();
                        sb.AppendLine($"- {(fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", fails))}");
                        sb.AppendLine($"- Took {(DateTime.Now - started).TotalSeconds:0} s · look-ups: {string.Join(", ", reply.LookUps.Select(l => l.Tool + (l.Args.Count > 0 ? " " + l.Args.ToJsonString() : "")))}");
                        sb.AppendLine();
                        sb.AppendLine("```text\n" + answer.Trim() + "\n```");
                        sb.AppendLine();
                        File.WriteAllText(Out, sb.ToString());
                    }
            }
            sb.Insert(0, $"**{score.Count(x => x.Pass)} of {score.Count} passed the rule checks.** " + string.Join(" · ", score.Where(x => !x.Pass).Select(x => $"{x.Id}: {x.Why}")) + "\n\n");
            File.WriteAllText(Out, sb.ToString());
        }
        finally
        {
            Relay.Features.UseActions2 = useActions2;
            Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", null);
        }
    }
}
