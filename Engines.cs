using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

// Speech engines. Program.cs picks one from Settings.Engine and falls back to spd when it goes quiet.
public interface ISpeechEngine
{
    // true = the text came out (possibly partly via a fallback); false = nothing was spoken at all
    bool Speak(string text, Settings s);
}

// Free Google Translate voice: one MP3 per sentence chunk, played by ffplay. Needs internet.
public sealed class GoogleEngine : ISpeechEngine
{
    private const int SampleRate = 24000; // translate_tts always returns 24 kHz MP3

    public bool Speak(string text, Settings s)
    {
        var chunks = TextChunks.Split(text).ToList();
        if (chunks.Count == 0)
        {
            return true;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.Timeout = TimeSpan.FromSeconds(10);

        int total = chunks.Count;
        SpeakStatus.Set("fetch", total == 1 ? "Google · fetching…" : $"Google · fetching 1/{total}…", 6_000);

        Task<byte[]>? next = Fetch(http, chunks[0], s.GoogleLang);
        for (int i = 0; i < chunks.Count; i++)
        {
            byte[] mp3;
            try
            {
                mp3 = next!.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // offline or rate-limited mid-read: NEVER drop the rest — the offline voice finishes it
                if (i == 0)
                {
                    SpeakStatus.Alert("Google voice unreachable");
                    return false; // nothing spoken yet: the caller hands the whole text to spd
                }

                SpeakStatus.Alert($"Google failed at {i + 1}/{total} — finishing offline");
                new SpdEngine().Speak(string.Join(" ", chunks.Skip(i)), s);
                return true;
            }

            next = i + 1 < chunks.Count ? Fetch(http, chunks[i + 1], s.GoogleLang) : null; // prefetch while this plays

            string f = Path.Combine(Path.GetTempPath(), $"readaloud-{i}.mp3");
            File.WriteAllBytes(f, mp3);

            // re-read speed/pitch per chunk so a tray click lands at the NEXT sentence,
            // even in the middle of a long read — not on some future read
            Settings live = Settings.Load();
            string filter = Filter(live.Speed, live.Pitch);
            SpeakStatus.Set("play",
                total == 1 ? "Google · playing" : $"Google · playing {i + 1}/{total}",
                10_000);
            string[] play = ["-nodisp", "-autoexit", "-loglevel", "quiet", f];
            Sh.Run("ffplay", filter.Length == 0 ? play : ["-af", filter, .. play]);
            try { File.Delete(f); } catch (Exception) { }
        }

        SpeakStatus.Clear();
        return true;
    }

    // ffplay -af chain; empty string = play the file untouched.
    // atempo only accepts 0.5..2.0 per stage, so anything bigger is a chain of stages.
    // Pitch shifts by resampling (asetrate), which drags tempo along with it, so the tempo
    // chain divides that back out: net tempo = speed, net pitch = pitch.
    public static string Filter(double speed, double pitch)
    {
        bool shift = Math.Abs(pitch - 1.0) > 0.001;
        var parts = new List<string>();
        if (shift)
        {
            parts.Add($"asetrate={(int)Math.Round(SampleRate * pitch)}");
        }

        parts.AddRange(Atempo(shift ? speed / pitch : speed));
        if (shift)
        {
            parts.Add($"aresample={SampleRate}");
        }

        return string.Join(",", parts);
    }

    private static IEnumerable<string> Atempo(double f)
    {
        while (f > 2.0)
        {
            yield return "atempo=2.0";
            f /= 2.0;
        }

        while (f < 0.5)
        {
            yield return "atempo=0.5";
            f /= 0.5;
        }

        if (Math.Abs(f - 1.0) > 0.001)
        {
            yield return "atempo=" + f.ToString("0.0###", CultureInfo.InvariantCulture);
        }
    }

    // `ReadAloud --print-filter` runs this; returns false if the chain builder drifted.
    public static bool SelfCheck()
    {
        (double Speed, double Pitch, string Want)[] cases =
        [
            (0.75, 1.0, "atempo=0.75"),
            (1.0, 1.0, ""),
            (2.0, 1.0, "atempo=2.0"),
            (2.5, 1.0, "atempo=2.0,atempo=1.25"),
            (3.0, 1.0, "atempo=2.0,atempo=1.5"),
            (0.25, 1.0, "atempo=0.5,atempo=0.5"),
            (2.5, 1.2, "asetrate=28800,atempo=2.0,atempo=1.0417,aresample=24000"),
        ];

        bool ok = true;
        foreach (var (speed, pitch, want) in cases)
        {
            string got = Filter(speed, pitch);
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} speed={speed} pitch={pitch} -> \"{got}\""
                              + (pass ? "" : $" (want \"{want}\")"));
        }

        // same speed knob, offline engine: spd-say -r
        (double Speed, int SpdRate, int Want)[] rates =
        [
            (0.5, 0, -100),
            (1.0, 0, 0),
            (1.0, 30, 30),
            (2.5, 0, 75),
            (3.0, 0, 100),
            (0.75, 20, -40),
        ];

        foreach (var (speed, spdRate, want) in rates)
        {
            int got = SpdEngine.Rate(new Settings { Speed = speed, SpdRate = spdRate });
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} speed={speed} SpdRate={spdRate} -> spd -r {got}"
                              + (pass ? "" : $" (want {want})"));
        }

        return ok;
    }

    private static Task<byte[]> Fetch(HttpClient http, string chunk, string lang) =>
        http.GetByteArrayAsync("https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob"
                               + $"&tl={lang}&q={Uri.EscapeDataString(chunk)}");
}

// Local neural voice: Inflect-Micro-v2 via its official ONNX package
// (https://huggingface.co/owensong/Inflect-Micro-v2). Needs python (uv-managed venv),
// ~40 MB of weights under ~/.local/share/readaloud/, and ffplay. A warm worker keeps
// the model loaded so each speak is fast after the first.
public sealed class InflectEngine : ISpeechEngine
{
    private const string HubId = "owensong/Inflect-Micro-v2-ONNX";

    public static string ShareDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "readaloud");

    public static string ModelDir => Path.Combine(ShareDir, "inflect-micro-v2");
    public static string VenvDir => Path.Combine(ShareDir, "inflect-venv");
    public static string Python => Path.Combine(VenvDir, "bin", "python");
    public static string InferScript => Path.Combine(ModelDir, "onnx", "inference_onnx.py");
    public static string MarkerPath => Path.Combine(ShareDir, "inflect.ready");
    public static string WorkerScript => Path.Combine(ShareDir, "inflect_worker.py");
    public static string WorkerPidPath => Path.Combine(ShareDir, "inflect-worker.pid");

    public static string SockPath
    {
        get
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string dir = !string.IsNullOrEmpty(runtime) ? runtime : Path.GetTempPath();
            return Path.Combine(dir, "readaloud-inflect.sock");
        }
    }

    // Ship path next to the published binary (install.sh / first EnsureReady copies it into ShareDir).
    public static string BundledWorker =>
        Path.Combine(AppContext.BaseDirectory, "inflect_worker.py");

    public bool Speak(string text, Settings s)
    {
        SpeakStatus.Set("prep", "Inflect · checking worker…", 4_000);
        if (!EnsureReady() || !EnsureWorker())
        {
            SpeakStatus.Alert("Inflect not ready — falling back");
            return false;
        }

        // Progressive like Google: one sentence (or ~180 chars) at a time so a long
        // paste starts talking in a few seconds instead of freezing until the whole
        // block has been synthesized. The worker is single-threaded, so no prefetch.
        var chunks = TextChunks.Split(text).ToList();
        if (chunks.Count == 0)
        {
            SpeakStatus.Clear();
            return true;
        }

        // Inflect's own speed is 0.5..2.0 (better than atempo). Past 2.0 / pitch ≠ 1 → ffplay.
        double modelSpeed = Math.Clamp(s.Speed, 0.5, 2.0);
        Settings live = Settings.Load();
        double variation = Math.Clamp(live.InflectVariation, 0.0, 1.0);
        int seed = live.InflectSeed;
        int total = chunks.Count;
        string prefix = Path.Combine(Path.GetTempPath(), $"readaloud-inflect-{Environment.ProcessId}");
        bool any = false;

        try
        {
            for (int i = 0; i < total; i++)
            {
                string wav = $"{prefix}-{i}.wav";
                try { if (File.Exists(wav)) File.Delete(wav); } catch (Exception) { }

                string preview = Preview(chunks[i]);
                SpeakStatus.Set("synth",
                    total == 1
                        ? $"Inflect · synthesizing… ({chunks[i].Length} chars)"
                        : $"Inflect · synthesizing {i + 1}/{total} — {preview}",
                    expireMs: 30_000);

                string req = JsonReq(chunks[i], wav, modelSpeed, variation, seed);
                int timeoutMs = SynthTimeoutMs(chunks[i].Length);
                if (!WorkerCall(req, timeoutMs))
                {
                    // worker may have died mid-flight — one restart, one retry on this chunk
                    SpeakStatus.Set("load", "Inflect · worker died, restarting…", 10_000);
                    StopWorker();
                    if (!EnsureWorker() || !WorkerCall(req, timeoutMs))
                    {
                        if (any)
                        {
                            // already spoke some: hand the rest to offline rather than silence
                            SpeakStatus.Alert($"Inflect failed at {i + 1}/{total} — finishing offline");
                            new SpdEngine().Speak(string.Join(" ", chunks.Skip(i)), s);
                            return true;
                        }

                        SpeakStatus.Alert("Inflect synth failed");
                        return false;
                    }
                }

                if (!File.Exists(wav) || new FileInfo(wav).Length < 44)
                {
                    if (any)
                    {
                        SpeakStatus.Alert($"Inflect empty audio at {i + 1}/{total} — finishing offline");
                        new SpdEngine().Speak(string.Join(" ", chunks.Skip(i)), s);
                        return true;
                    }

                    SpeakStatus.Alert("Inflect produced no audio");
                    return false;
                }

                live = Settings.Load(); // tray speed/pitch mid-read lands on this play
                string filter = GoogleEngine.Filter(live.Speed / modelSpeed, live.Pitch);
                SpeakStatus.Set("play",
                    total == 1
                        ? "Inflect · playing"
                        : $"Inflect · playing {i + 1}/{total} — {preview}",
                    expireMs: 15_000);
                string[] play = ["-nodisp", "-autoexit", "-loglevel", "quiet", wav];
                Sh.Run("ffplay", filter.Length == 0 ? play : ["-af", filter, .. play]);
                any = true;

                try { File.Delete(wav); } catch (Exception) { }
            }

            SpeakStatus.Clear();
            return true;
        }
        finally
        {
            // leave no pile of wavs if we were killed mid-loop
            for (int i = 0; i < total; i++)
            {
                try { File.Delete($"{prefix}-{i}.wav"); } catch (Exception) { }
            }
        }
    }

    private static string Preview(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= 42 ? s : s[..39] + "…";
    }

    // neural synth is roughly linear in chars; give headroom without hanging forever
    private static int SynthTimeoutMs(int chars) =>
        Math.Clamp(25_000 + chars * 100, 30_000, 180_000);

    private static string JsonReq(string text, string output, double speed, double variation, int seed)
    {
        // manual JSON so we don't pull System.Text.Json into a hot path with escaping traps —
        // text can contain quotes; use System.Text.Json for the one field that needs it.
        string t = System.Text.Json.JsonSerializer.Serialize(text);
        string o = System.Text.Json.JsonSerializer.Serialize(output);
        return "{\"text\":" + t
               + ",\"output\":" + o
               + ",\"speed\":" + speed.ToString("0.###", CultureInfo.InvariantCulture)
               + ",\"variation\":" + variation.ToString("0.###", CultureInfo.InvariantCulture)
               + ",\"seed\":" + seed.ToString(CultureInfo.InvariantCulture)
               + "}\n";
    }

    private static bool WorkerCall(string requestLine, int receiveTimeoutMs = 120_000)
    {
        try
        {
            using var client = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            client.ReceiveTimeout = receiveTimeoutMs;
            client.SendTimeout = 10_000;
            client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SockPath));
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(requestLine);
            client.Send(payload);

            var buf = new byte[4096];
            int n = client.Receive(buf);
            string resp = System.Text.Encoding.UTF8.GetString(buf, 0, n).Trim();
            if (resp.StartsWith("ok", StringComparison.Ordinal))
            {
                return true;
            }

            Console.Error.WriteLine("inflect worker: " + resp);
            return false;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("inflect worker: " + e.Message);
            return false;
        }
    }

    private static bool WorkerPing()
    {
        try
        {
            using var client = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            client.ReceiveTimeout = 2_000;
            client.SendTimeout = 2_000;
            client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SockPath));
            client.Send(System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"ping\"}\n"));
            var buf = new byte[64];
            int n = client.Receive(buf);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n).Trim()
                .StartsWith("ok", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ReadyFlagPath => SockPath + ".ready";

    public static bool EnsureWorker()
    {
        if (WorkerAlive())
        {
            return true;
        }

        try
        {
            SpeakStatus.Set("load", "Inflect · starting worker (model load can take ~30s)…", 60_000);
            InstallWorkerScript();
            try { if (File.Exists(SockPath)) File.Delete(SockPath); } catch (Exception) { }
            try { if (File.Exists(ReadyFlagPath)) File.Delete(ReadyFlagPath); } catch (Exception) { }

            string log = Path.Combine(ShareDir, "inflect-worker.log");
            // bash detaches fully so this C# process can exit without reaping/killing the worker
            string script =
                $"export READALOUD_INFLECT_MODEL={BashQuote(ModelDir)}; "
                + $"export READALOUD_INFLECT_SOCK={BashQuote(SockPath)}; "
                + $"export READALOUD_INFLECT_READY={BashQuote(ReadyFlagPath)}; "
                + $"setsid {BashQuote(Python)} {BashQuote(WorkerScript)} >>{BashQuote(log)} 2>&1 < /dev/null & echo $!";
            var psi = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(script);
            DecoratePath(psi);

            using var starter = Process.Start(psi)!;
            string pidText = starter.StandardOutput.ReadToEnd().Trim();
            starter.WaitForExit();
            if (!int.TryParse(pidText, out int pid))
            {
                Fail("could not start Inflect worker (no pid). See " + log);
                return false;
            }

            File.WriteAllText(WorkerPidPath, pid.ToString(CultureInfo.InvariantCulture));

            // wait for model load (ready flag) or process death, up to 2 minutes —
            // surface elapsed time so it never looks frozen
            var started = DateTime.UtcNow;
            var deadline = started.AddMinutes(2);
            int lastAnnounced = -1;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(ReadyFlagPath) && File.Exists(SockPath) && WorkerPing())
                {
                    SpeakStatus.Set("ready", "Inflect · worker warm", 2_500);
                    return true;
                }

                try
                {
                    if (Process.GetProcessById(pid).HasExited)
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    break;
                }

                int sec = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (sec >= 2 && sec / 3 != lastAnnounced)
                {
                    lastAnnounced = sec / 3;
                    SpeakStatus.Set("load",
                        $"Inflect · loading model… {sec}s (see {Path.GetFileName(log)})",
                        15_000);
                }

                Thread.Sleep(150);
            }

            Fail("Inflect worker failed to start. See " + log);
            StopWorker();
            return false;
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return false;
        }
    }

    private static string BashQuote(string s) =>
        "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static void StopWorker()
    {
        try
        {
            if (File.Exists(SockPath))
            {
                // polite quit
                try
                {
                    using var client = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.Unix,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Unspecified);
                    client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SockPath));
                    client.Send(System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"quit\"}\n"));
                    client.Receive(new byte[64]);
                }
                catch (Exception)
                {
                }
            }

            if (File.Exists(WorkerPidPath)
                && int.TryParse(File.ReadAllText(WorkerPidPath).Trim(), out int pid))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        try { if (File.Exists(WorkerPidPath)) File.Delete(WorkerPidPath); } catch (Exception) { }
        try { if (File.Exists(SockPath)) File.Delete(SockPath); } catch (Exception) { }
    }

    // PID + sock only. Do NOT ping here: the worker is single-threaded, so a ping during a
    // long synth would time out and look "dead", then we'd kill a healthy in-flight speak.
    private static bool WorkerAlive()
    {
        if (!File.Exists(SockPath) || !File.Exists(WorkerPidPath))
        {
            return false;
        }

        try
        {
            int pid = int.Parse(File.ReadAllText(WorkerPidPath).Trim(), CultureInfo.InvariantCulture);
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void InstallWorkerScript()
    {
        Directory.CreateDirectory(ShareDir);
        // prefer the copy next to the published binary; fall back to already-installed share copy
        if (File.Exists(BundledWorker))
        {
            File.Copy(BundledWorker, WorkerScript, overwrite: true);
        }
        else if (!File.Exists(WorkerScript))
        {
            throw new FileNotFoundException(
                "inflect_worker.py missing — rebuild with `dotnet publish` so it is copied next to the binary.");
        }
    }

    // Fast check: files present + marker written after a successful install/import.
    public static bool IsReady() =>
        File.Exists(MarkerPath)
        && File.Exists(Python)
        && File.Exists(InferScript)
        && File.Exists(Path.Combine(ModelDir, "onnx", "duration.onnx"));

    // Download weights + private python env. Idempotent. Notifies via notify-send when work happens.
    public static bool EnsureReady()
    {
        try
        {
            if (IsReady() && ImportOk())
            {
                InstallWorkerScript();
                return true;
            }

            Directory.CreateDirectory(ShareDir);
            bool worked = false;

            if (!File.Exists(InferScript) || !File.Exists(Path.Combine(ModelDir, "onnx", "duration.onnx")))
            {
                Notify("ReadAloud", "Downloading Inflect voice (~40 MB)…");
                if (!EnsureHfCli())
                {
                    Fail(
                        "need the Hugging Face CLI once.\n"
                        + "  uv tool install huggingface_hub\n"
                        + $"  then: hf download {HubId} --local-dir {ModelDir}");
                    return false;
                }

                if (!RunOk("hf", ["download", HubId, "--local-dir", ModelDir])
                    && !RunOk("huggingface-cli", ["download", HubId, "--local-dir", ModelDir]))
                {
                    Fail($"download failed. Try: hf download {HubId} --local-dir {ModelDir}");
                    return false;
                }

                worked = true;
            }

            if (!File.Exists(Python) || !ImportOk())
            {
                Notify("ReadAloud", "Setting up Inflect python env…");
                if (!EnsureUv())
                {
                    Fail("need `uv` (https://docs.astral.sh/uv/). curl -LsSf https://astral.sh/uv/install.sh | sh");
                    return false;
                }

                if (!File.Exists(Python) && !RunOk("uv", ["venv", VenvDir]))
                {
                    Fail("uv venv failed.");
                    return false;
                }

                if (!RunOk("uv",
                    [
                        "pip", "install", "--python", Python,
                        "numpy>=1.26,<3", "onnxruntime>=1.18,<2", "soundfile>=0.13",
                        "phonemizer>=3.3", "espeakng-loader>=0.2.4",
                        "num2words>=0.5.14", "Unidecode>=1.3.8",
                    ]))
                {
                    Fail("python package install failed.");
                    return false;
                }

                worked = true;
            }

            if (!ImportOk())
            {
                Fail("onnxruntime/phonemizer import still broken after install.");
                return false;
            }

            InstallWorkerScript();
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (worked)
            {
                Notify("ReadAloud", "Inflect voice is ready.");
            }

            return true;
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return false;
        }
    }

    public static string StatusLine()
    {
        string live = SpeakStatus.CurrentDetail();
        if (live.Length > 0)
        {
            return live;
        }

        if (!IsReady())
        {
            return "Inflect: not installed";
        }

        // cheap pid/sock check for the tray (full ping can stall a menu open if wedged)
        bool warm = File.Exists(SockPath) && File.Exists(WorkerPidPath);
        if (warm)
        {
            try
            {
                int pid = int.Parse(File.ReadAllText(WorkerPidPath).Trim(), CultureInfo.InvariantCulture);
                warm = !Process.GetProcessById(pid).HasExited;
            }
            catch (Exception)
            {
                warm = false;
            }
        }

        return warm ? "Inflect: ready (warm)" : "Inflect: ready (cold — first speak loads model)";
    }

    private static bool ImportOk()
    {
        if (!File.Exists(Python))
        {
            return false;
        }

        return RunOk(Python, ["-c", "import onnxruntime,phonemizer,soundfile,numpy"]);
    }

    private static bool EnsureUv()
    {
        if (HasCmd("uv"))
        {
            return true;
        }

        Notify("ReadAloud", "Installing uv (python toolchain)…");
        return RunOk("bash",
            ["-lc", "curl -LsSf https://astral.sh/uv/install.sh | sh"]);
    }

    private static bool EnsureHfCli()
    {
        if (HasCmd("hf") || HasCmd("huggingface-cli"))
        {
            return true;
        }

        if (!EnsureUv())
        {
            return false;
        }

        Notify("ReadAloud", "Installing Hugging Face CLI…");
        return RunOk("uv", ["tool", "install", "huggingface_hub"])
               && (HasCmd("hf") || HasCmd("huggingface-cli"));
    }

    private static bool HasCmd(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("bash", $"-lc \"command -v {cmd}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            DecoratePath(psi);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && o.Trim().Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Notify(string title, string body) =>
        Sh.Run("notify-send", "--app-name=ReadAloud", "-i", "audio-volume-high-symbolic", title, body);

    private static void Fail(string msg)
    {
        Console.Error.WriteLine("inflect: " + msg);
        Notify("ReadAloud — Inflect", msg.Split('\n')[0]);
    }

    private static void DecoratePath(ProcessStartInfo psi)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string local = Path.Combine(home, ".local", "bin");
        if (!path.Split(':').Contains(local))
        {
            psi.Environment["PATH"] = local + ":" + path;
        }
    }

    private static bool RunOk(string cmd, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            DecoratePath(psi);
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

// Shared sentence splitter (Google + Inflect progressive play).
internal static class TextChunks
{
    public static IEnumerable<string> Split(string s, int max = 180)
    {
        string cur = "";
        foreach (string part in Regex.Split(s, @"(?<=[.!?;:])\s+"))
        {
            string p = part;
            while (p.Length > max)
            {
                if (cur.Length > 0) { yield return cur; cur = ""; }
                yield return p[..max];
                p = p[max..];
            }
            if (cur.Length + p.Length + 1 > max)
            {
                if (cur.Length > 0) yield return cur;
                cur = p;
            }
            else
            {
                cur = cur.Length == 0 ? p : cur + " " + p;
            }
        }
        if (cur.Length > 0)
        {
            yield return cur;
        }
    }
}

// Offline speech-dispatcher robot. Always available, never pretty.
public sealed class SpdEngine : ISpeechEngine
{
    public bool Speak(string text, Settings s)
    {
        SpeakStatus.Set("play", "Offline · speaking…", 8_000);
        var psi = new ProcessStartInfo("spd-say") { RedirectStandardInput = true };
        psi.ArgumentList.Add("-e"); // pipe mode: text on stdin, no argv limits
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(Rate(s).ToString());
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Pitch(s.Pitch).ToString());
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit();
            SpeakStatus.Clear();
            return true;
        }
        catch (Exception)
        {
            SpeakStatus.Alert("Offline voice missing (spd-say)");
            return false; // speech-dispatcher missing entirely: nothing sensible left to do
        }
    }

    // Speed is a tempo multiplier, spd-say wants -100..100. Speed 1.0 keeps the user's SpdRate,
    // 0.5 bottoms out, 3.0 tops out. ponytail: straight lerp either side of 1.0, good enough for a robot voice.
    internal static int Rate(Settings s) => Clamp(s.Speed <= 1.0
        ? -100 + ((s.Speed - 0.5) / 0.5 * (s.SpdRate + 100))
        : s.SpdRate + ((s.Speed - 1.0) / 2.0 * (100 - s.SpdRate)));

    internal static int Pitch(double pitch) => Clamp((pitch - 1.0) * 100);

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -100, 100));
}

internal static class Sh
{
    public static void Run(string cmd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd);
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Exception)
        {
            // tool missing, ignore
        }
    }
}
