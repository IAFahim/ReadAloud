using System.Diagnostics;
using System.Text.RegularExpressions;

// Wiggle-to-read: select text, shake the mouse left-right, hear it.
//
// Reads mouse relative-motion events straight from /dev/input (evdev), so it works over any
// app on Wayland or X11 with near-zero overhead: one blocking read per hardware event, no
// polling. Needs the user in the `input` group: sudo usermod -aG input $USER, then re-login.
//
// A "wiggle" = FlipsToTrigger direction reversals, each after >= MinSegmentPx of travel,
// all inside WindowMs. Ordinary mousing is nearly straight lines — it never reverses that
// often that fast. Cooldown stops one shake from firing twice.
static class Wiggle
{
    static Settings cached = Settings.Load();
    static DateTime stamp;

    // settings re-read only when the file's mtime moves — stat on direction flips, never per event
    static Settings Current()
    {
        try
        {
            DateTime t = File.GetLastWriteTimeUtc(Settings.FilePath);
            if (t != stamp)
            {
                stamp = t;
                cached = Settings.Load();
            }
        }
        catch (Exception)
        {
        }
        return cached;
    }

    public static void Start()
    {
        foreach (string dev in MouseEventNodes())
        {
            new Thread(() => Watch(dev)) { IsBackground = true, Name = $"wiggle:{dev}" }.Start();
        }
    }

    static List<string> MouseEventNodes()
    {
        var nodes = new List<string>();
        try
        {
            // blocks look like: N: Name="..." ... H: Handlers=mouse0 event7
            foreach (string block in File.ReadAllText("/proc/bus/input/devices").Split("\n\n"))
            {
                if (!Regex.IsMatch(block, @"Handlers=.*\bmouse\d"))
                {
                    continue;
                }
                Match m = Regex.Match(block, @"\bevent(\d+)\b");
                if (m.Success)
                {
                    nodes.Add($"/dev/input/event{m.Groups[1].Value}");
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"wiggle: cannot list input devices: {e.Message}");
        }

        if (nodes.Count == 0)
        {
            Console.Error.WriteLine("wiggle: no mouse devices found");
        }
        return nodes;
    }

    static void Watch(string dev)
    {
        try
        {
            using var fs = new FileStream(dev, FileMode.Open, FileAccess.Read);
            Console.Error.WriteLine($"wiggle: watching {dev}");
            var detector = new WiggleDetector();
            var buf = new byte[24]; // struct input_event on 64-bit: 16B timeval + type + code + value
            while (ReadFull(fs, buf))
            {
                ushort type = BitConverter.ToUInt16(buf, 16);
                ushort code = BitConverter.ToUInt16(buf, 18);
                int value = BitConverter.ToInt32(buf, 20);
                if (type != 2 || code != 0) // EV_REL REL_X only
                {
                    continue;
                }
                Settings s = Current();
                if (detector.Feed(value, Environment.TickCount64, s.WiggleFlips, s.WiggleWindowMs))
                {
                    Trigger(s);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"wiggle: no read permission on {dev} — run: sudo usermod -aG input $USER  then log out and back in");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"wiggle: {dev}: {e.Message}");
        }
    }

    static bool ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0)
            {
                return false;
            }
            got += n;
        }
        return true;
    }

    static void Trigger(Settings s)
    {
        if (!s.WiggleEnabled)
        {
            return;
        }

        if (s.WigglePop)
        {
            try // a tiny pop so the shake feels acknowledged before the voice warms up
            {
                Process.Start(new ProcessStartInfo("ffplay",
                    "-f lavfi -i sine=frequency=880:duration=0.07 -autoexit -nodisp -loglevel quiet"));
            }
            catch (Exception)
            {
                // no ffplay, no pop — the speech itself is the feedback
            }
        }

        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!);
            psi.ArgumentList.Add("--wiggle"); // wiggle etiquette: stop if talking, skip stale text
            Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"wiggle: trigger failed: {e.Message}");
        }
    }
}

// Pure logic, no I/O — exercised by `ReadAloud --wiggle-test`.
sealed class WiggleDetector
{
    const int MinSegmentPx = 15;
    const int CooldownMs = 1500;

    int dir;
    double travel;
    long lastTrigger = -CooldownMs;
    readonly Queue<long> flips = new();

    // feed one horizontal delta; true = that delta completed a wiggle
    public bool Feed(int dx, long nowMs, int flipsToTrigger = 4, int windowMs = 600)
    {
        int s = Math.Sign(dx);
        if (s == 0)
        {
            return false;
        }
        if (s == dir)
        {
            travel += Math.Abs(dx);
            return false;
        }

        bool triggered = false;
        if (dir != 0 && travel >= MinSegmentPx)
        {
            flips.Enqueue(nowMs);
            while (flips.Count > 0 && nowMs - flips.Peek() > windowMs)
            {
                flips.Dequeue();
            }
            if (flips.Count >= flipsToTrigger && nowMs - lastTrigger > CooldownMs)
            {
                lastTrigger = nowMs;
                flips.Clear();
                triggered = true;
            }
        }

        dir = s;
        travel = Math.Abs(dx);
        return triggered;
    }

    public static bool SelfCheck()
    {
        bool ok = true;
        void Check(string name, bool cond)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}");
            ok &= cond;
        }

        // a real shake: 5 strokes of 40px, alternating, 80ms apart -> triggers
        var d = new WiggleDetector();
        bool hit = false;
        int sign = 1;
        for (int i = 0; i < 6; i++, sign = -sign)
        {
            for (int j = 0; j < 4; j++)
            {
                hit |= d.Feed(sign * 10, i * 80 + j * 10);
            }
        }
        Check("deliberate shake triggers", hit);

        // straight fast mousing: one long sweep -> never
        d = new WiggleDetector();
        hit = false;
        for (int i = 0; i < 100; i++)
        {
            hit |= d.Feed(25, i * 8);
        }
        Check("straight sweep never triggers", !hit);

        // slow direction changes (normal aiming): 1 flip per 400ms -> never
        d = new WiggleDetector();
        hit = false;
        sign = 1;
        for (int i = 0; i < 10; i++, sign = -sign)
        {
            hit |= d.Feed(sign * 40, i * 400);
        }
        Check("slow zigzag never triggers", !hit);

        // tiny jitter (hand tremor, 3px strokes) -> never
        d = new WiggleDetector();
        hit = false;
        sign = 1;
        for (int i = 0; i < 40; i++, sign = -sign)
        {
            hit |= d.Feed(sign * 3, i * 30);
        }
        Check("small jitter never triggers", !hit);

        // cooldown: two shakes back-to-back -> exactly one trigger until cooldown passes
        d = new WiggleDetector();
        int count = 0;
        sign = 1;
        for (int i = 0; i < 12; i++, sign = -sign)
        {
            if (d.Feed(sign * 40, i * 80))
            {
                count++;
            }
        }
        Check($"cooldown limits rapid-fire (got {count})", count == 1);

        return ok;
    }
}
