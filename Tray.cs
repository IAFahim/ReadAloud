using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;

// Top-bar indicator app: `ReadAloud --tray`.
// Owned by the tray agent; the speaker side never calls anything here except Run().
//
// It is a StatusNotifierItem spoken straight onto the session bus (org.kde.StatusNotifierItem +
// com.canonical.dbusmenu, registered with org.kde.StatusNotifierWatcher). GNOME's AppIndicator
// extension draws it. No GTK, no libayatana, no native deps — one NuGet package.
//
// Settings.cs is the only shared state: every menu click re-loads the file, edits it, saves it.
static class Tray
{
    const string ItemIface = "org.kde.StatusNotifierItem";
    const string MenuIface = "com.canonical.dbusmenu";
    const string ItemPath = "/StatusNotifierItem";
    const string MenuPath = "/MenuBar";
    const string Icon = "audio-volume-high-symbolic";

    static DBusConnection conn = null!;
    static Settings cfg = Settings.Load();
    static uint revision = 1;

    // One flat menu table. Id 0 is the invisible root; parents are implied by Parent.
    // On = is it ticked right now (read fresh from cfg), Click = what pressing it does.
    sealed record Node(int Id, int Parent, string Label, string Toggle = "", Func<bool>? On = null, Action? Click = null);

    static readonly Node[] Nodes = BuildMenu();

    static Node[] BuildMenu()
    {
        var menu = new List<Node>
        {
            new(1, 0, "Stop speaking", Click: () => Start(Environment.ProcessPath!, "--stop")),
            new(2, 0, "Mute Claude replies", "checkmark", () => cfg.MuteClaude, () => Edit(s => s.MuteClaude = !s.MuteClaude)),
            new(3, 0, "Speed"),
            new(4, 0, "Engine"),
            new(41, 4, "Google voice", "radio", () => cfg.Engine == "google", () => Edit(s => s.Engine = "google")),
            new(42, 4, "Offline voice", "radio", () => cfg.Engine == "spd", () => Edit(s => s.Engine = "spd")),
            new(5, 0, "Wiggle to read", "checkmark", () => cfg.WiggleEnabled, () => Edit(s => s.WiggleEnabled = !s.WiggleEnabled)),
            new(6, 0, "Open settings file", Click: () => Start("xdg-open", Settings.FilePath)),
            new(7, 0, "Quit", Click: Quit),
        };

        int id = 31;
        foreach (double preset in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
        {
            double v = preset;
            menu.Add(new(id++, 3, $"{v:0.0}x", "radio", () => Math.Abs(cfg.Speed - v) < 0.01, () => Edit(s => s.Speed = v)));
        }

        return menu.OrderBy(n => n.Id).ToArray(); // ids are the display order inside each parent
    }

    public static void Run()
    {
        Wiggle.Start(); // the tray is the resident process, so it hosts the wiggle watcher too
        RunAsync().GetAwaiter().GetResult();
    }

    static async Task RunAsync()
    {
        conn = new DBusConnection(DBusAddress.Session
                                  ?? throw new InvalidOperationException("no session bus (DBUS_SESSION_BUS_ADDRESS unset)"));
        await conn.ConnectAsync();
        conn.AddMethodHandler(new Handler(ItemPath, HandleItem));
        conn.AddMethodHandler(new Handler(MenuPath, HandleMenu));

        string name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
        await conn.RequestNameAsync(name, RequestNameOptions.None);

        // Autostart can beat gnome-shell to the bus, and the shell restarts — so wait for the
        // watcher and re-register every time it comes back instead of registering once and hoping.
        using var watcher = await conn.WatchNameOwnerAsync("org.kde.StatusNotifierWatcher");
        while (true)
        {
            string owner = await watcher.WaitForOwnerAsync(default);
            try
            {
                await conn.CallMethodAsync(RegisterMessage(name));
                Console.Error.WriteLine($"registered {name} with the StatusNotifierWatcher");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"register failed: {e.Message}");
            }

            try
            {
                await Task.Delay(Timeout.Infinite, watcher.GetOwnerChangedCancellationToken(owner));
            }
            catch (OperationCanceledException)
            {
                // watcher died (shell restart): loop round and register with the new one
            }
        }
    }

    static MessageBuffer RegisterMessage(string name)
    {
        using var w = conn.GetMessageWriter();
        w.WriteMethodCallHeader("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
            "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s", MessageFlags.None);
        w.WriteString(name);
        return w.CreateMessage();
    }

    // ---- org.kde.StatusNotifierItem ----

    static readonly string[] ItemProps =
        { "Category", "Id", "Title", "Status", "IconName", "IconThemePath", "ItemIsMenu", "Menu" };

    static void HandleItem(MethodContext ctx)
    {
        Message m = ctx.Request;

        if (ctx.IsDBusIntrospectRequest)
        {
            ctx.ReplyIntrospectXml(new[] { Utf8(ItemXml), IntrospectionXml.DBusProperties }, Array.Empty<string>());
            return;
        }

        if (ctx.IsPropertiesInterfaceRequest)
        {
            Reader r = m.GetBodyReader();
            r.ReadString(); // interface name: we only serve the one
            if (m.MemberAsString == "Get")
            {
                string prop = r.ReadString();
                if (!ItemProps.Contains(prop))
                {
                    ctx.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", prop);
                    return;
                }
                MessageWriter w = ctx.CreateReplyWriter("v");
                try
                {
                    WriteItemProp(ref w, prop);
                    ctx.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                return;
            }
            if (m.MemberAsString == "GetAll")
            {
                MessageWriter w = ctx.CreateReplyWriter("a{sv}");
                try
                {
                    ArrayStart d = w.WriteDictionaryStart();
                    foreach (string prop in ItemProps)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(prop);
                        WriteItemProp(ref w, prop);
                    }
                    w.WriteDictionaryEnd(d);
                    ctx.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                return;
            }
        }

        // Activate / SecondaryActivate / ContextMenu / Scroll: ItemIsMenu=true, so the shell just
        // opens the menu. Ack them so no client hangs.
        if (m.InterfaceAsString == ItemIface)
        {
            ReplyEmpty(ctx);
            return;
        }

        ctx.ReplyUnknownMethodError();
    }

    static void WriteItemProp(ref MessageWriter w, string prop)
    {
        switch (prop)
        {
            case "Category": w.WriteVariantString("ApplicationStatus"); break;
            case "Id": w.WriteVariantString("readaloud"); break;
            case "Title": w.WriteVariantString("ReadAloud"); break;
            case "Status": w.WriteVariantString("Active"); break;
            case "IconName": w.WriteVariantString(Icon); break;
            case "IconThemePath": w.WriteVariantString(""); break;
            case "ItemIsMenu": w.WriteVariantBool(true); break;
            case "Menu": w.WriteVariantObjectPath(MenuPath); break;
            default: throw new ArgumentOutOfRangeException(nameof(prop), prop);
        }
    }

    // ---- com.canonical.dbusmenu ----

    static void HandleMenu(MethodContext ctx)
    {
        Message m = ctx.Request;

        if (ctx.IsDBusIntrospectRequest)
        {
            ctx.ReplyIntrospectXml(new[] { Utf8(MenuXml), IntrospectionXml.DBusProperties }, Array.Empty<string>());
            return;
        }

        if (ctx.IsPropertiesInterfaceRequest)
        {
            Reader r = m.GetBodyReader();
            r.ReadString();
            switch (m.MemberAsString)
            {
                case "Get":
                {
                    string prop = r.ReadString();
                    using var w = ctx.CreateReplyWriter("v");
                    switch (prop)
                    {
                        case "Version": w.WriteVariantUInt32(3); break;
                        case "TextDirection": w.WriteVariantString("ltr"); break;
                        case "Status": w.WriteVariantString("normal"); break;
                        case "IconThemePath": w.WriteSignature("as"); w.WriteArray(Array.Empty<string>()); break;
                        default: ctx.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", prop); return;
                    }
                    ctx.Reply(w.CreateMessage());
                    return;
                }
                case "GetAll":
                {
                    MessageWriter w = ctx.CreateReplyWriter("a{sv}");
                    try
                    {
                        ArrayStart d = w.WriteDictionaryStart();
                        Entry(ref w, "Version", 3u);
                        Entry(ref w, "TextDirection", "ltr");
                        Entry(ref w, "Status", "normal");
                        w.WriteDictionaryEntryStart();
                        w.WriteString("IconThemePath");
                        w.WriteSignature("as");
                        w.WriteArray(Array.Empty<string>());
                        w.WriteDictionaryEnd(d);
                        ctx.Reply(w.CreateMessage());
                    }
                    finally { w.Dispose(); }
                    return;
                }
            }
        }

        if (m.InterfaceAsString != MenuIface)
        {
            ctx.ReplyUnknownMethodError();
            return;
        }

        switch (m.MemberAsString)
        {
            case "GetLayout":
            {
                Reader r = m.GetBodyReader();
                int parent = r.ReadInt32();
                int depth = r.ReadInt32();
                cfg = Settings.Load(); // another process may have edited the file since last time
                MessageWriter w = ctx.CreateReplyWriter("u(ia{sv}av)");
                try
                {
                    w.WriteUInt32(revision);
                    WriteNode(ref w, parent, depth);
                    ctx.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                return;
            }

            case "GetGroupProperties":
            {
                Reader r = m.GetBodyReader();
                int[] ids = r.ReadArrayOfInt32();
                cfg = Settings.Load();
                MessageWriter w = ctx.CreateReplyWriter("a(ia{sv})");
                try
                {
                    ArrayStart a = w.WriteArrayStart(DBusType.Struct);
                    foreach (int id in ids.Length == 0 ? Nodes.Select(n => n.Id) : ids)
                    {
                        w.WriteStructureStart();
                        w.WriteInt32(id);
                        WriteProps(ref w, id);
                    }
                    w.WriteArrayEnd(a);
                    ctx.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                return;
            }

            case "Event":
            {
                Reader r = m.GetBodyReader();
                int id = r.ReadInt32();
                string ev = r.ReadString();
                ReplyEmpty(ctx); // ack first: a click may spawn a process or quit us
                if (ev == "clicked")
                {
                    Click(id);
                }
                return;
            }

            case "EventGroup":
            {
                Reader r = m.GetBodyReader();
                ArrayEnd end = r.ReadArrayStart(DBusType.Struct);
                var clicked = new List<int>();
                while (r.HasNext(end))
                {
                    r.AlignStruct();
                    int id = r.ReadInt32();
                    string ev = r.ReadString();
                    r.ReadVariantValue();
                    r.ReadUInt32();
                    if (ev == "clicked")
                    {
                        clicked.Add(id);
                    }
                }
                using (var w = ctx.CreateReplyWriter("ai"))
                {
                    w.WriteArray(Array.Empty<int>()); // no ids failed
                    ctx.Reply(w.CreateMessage());
                }
                clicked.ForEach(Click);
                return;
            }

            case "AboutToShow":
            {
                bool changed = RefreshFromDisk();
                using var w = ctx.CreateReplyWriter("b");
                w.WriteBool(changed); // true only when the file really moved under us
                ctx.Reply(w.CreateMessage());
                return;
            }

            case "AboutToShowGroup":
            {
                Reader r = m.GetBodyReader();
                int[] ids = r.ReadArrayOfInt32();
                bool changed = RefreshFromDisk();
                using var w = ctx.CreateReplyWriter("aiai");
                w.WriteArray(changed ? ids : Array.Empty<int>());
                w.WriteArray(Array.Empty<int>()); // none of them errored
                ctx.Reply(w.CreateMessage());
                return;
            }
        }

        ctx.ReplyUnknownMethodError();
    }

    // (ia{sv}av) — id, properties, children. depth -1 = the whole tree, 0 = this node only.
    static void WriteNode(ref MessageWriter w, int id, int depth)
    {
        w.WriteStructureStart();
        w.WriteInt32(id);
        WriteProps(ref w, id);
        ArrayStart children = w.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
        {
            foreach (Node child in Nodes.Where(n => n.Parent == id))
            {
                w.WriteSignature("(ia{sv}av)");
                WriteNode(ref w, child.Id, depth - 1);
            }
        }
        w.WriteArrayEnd(children);
    }

    // ponytail: the propertyNames filter from GetLayout/GetGroupProperties is ignored — it is only
    // an optimisation and every client copes with being handed the full property set.
    static void WriteProps(ref MessageWriter w, int id)
    {
        Node? n = Nodes.FirstOrDefault(x => x.Id == id);
        ArrayStart d = w.WriteDictionaryStart();
        if (n != null)
        {
            Entry(ref w, "label", n.Label);
            if (n.Toggle.Length > 0)
            {
                Entry(ref w, "toggle-type", n.Toggle);
                Entry(ref w, "toggle-state", n.On!() ? 1 : 0);
            }
        }
        if (Nodes.Any(x => x.Parent == id))
        {
            Entry(ref w, "children-display", "submenu");
        }
        w.WriteDictionaryEnd(d);
    }

    static void Entry(ref MessageWriter w, string key, string value)
    {
        w.WriteDictionaryEntryStart();
        w.WriteString(key);
        w.WriteVariantString(value);
    }

    static void Entry(ref MessageWriter w, string key, int value)
    {
        w.WriteDictionaryEntryStart();
        w.WriteString(key);
        w.WriteVariantInt32(value);
    }

    static void Entry(ref MessageWriter w, string key, uint value)
    {
        w.WriteDictionaryEntryStart();
        w.WriteString(key);
        w.WriteVariantUInt32(value);
    }

    // ---- actions ----

    static void Click(int id) => Nodes.FirstOrDefault(n => n.Id == id)?.Click?.Invoke();

    static void Edit(Action<Settings> change)
    {
        Settings s = Settings.Load(); // never save a stale copy over someone else's edit
        change(s);
        s.Save();
        cfg = s;
        SignalLayoutUpdated(); // a click really changed state: tell any open menu the ticks moved
    }

    // Reload from disk; only announce a layout change when the tick-relevant state truly moved.
    // Announcing on every AboutToShow makes gnome-shell rebuild the menu mid-hover, which kills
    // submenus before they can open — that was the "Speed/Engine won't expand" bug.
    static bool RefreshFromDisk()
    {
        Settings fresh = Settings.Load();
        bool changed = fresh.MuteClaude != cfg.MuteClaude
                       || fresh.Engine != cfg.Engine
                       || Math.Abs(fresh.Speed - cfg.Speed) > 0.001;
        cfg = fresh;
        if (changed)
        {
            SignalLayoutUpdated();
        }
        return changed;
    }

    static void SignalLayoutUpdated()
    {
        revision++;
        try
        {
            using var w = conn.GetMessageWriter();
            w.WriteSignalHeader(null!, MenuPath, MenuIface, "LayoutUpdated", "ui");
            w.WriteUInt32(revision);
            w.WriteInt32(0);
            conn.TrySendMessage(w.CreateMessage());
        }
        catch (Exception)
        {
            // not connected yet / bus gone: the menu just refreshes on the next AboutToShow
        }
    }

    static void Quit() => Task.Run(async () =>
    {
        await Task.Delay(300); // let the D-Bus reply leave the socket first
        Environment.Exit(0);
    });

    static void Start(string cmd, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd);
            psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{cmd} failed: {e.Message}");
        }
    }

    // ---- plumbing ----

    sealed class Handler(string path, Action<MethodContext> handle) : IPathMethodHandler
    {
        public string Path => path;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                handle(context);
                if (!context.ReplySent && !context.NoReplyExpected)
                {
                    context.ReplyUnknownMethodError();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{context.Request.MemberAsString}: {e}");
                if (!context.ReplySent && !context.NoReplyExpected)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", e.Message);
                }
            }
            return default;
        }
    }

    static void ReplyEmpty(MethodContext ctx)
    {
        using var w = ctx.CreateReplyWriter("");
        ctx.Reply(w.CreateMessage());
    }

    static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    const string ItemXml = """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <method name="Activate">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="SecondaryActivate">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="ContextMenu">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="Scroll">
            <arg name="delta" type="i" direction="in"/>
            <arg name="orientation" type="s" direction="in"/>
          </method>
          <signal name="NewIcon"/>
          <signal name="NewTitle"/>
          <signal name="NewStatus">
            <arg name="status" type="s"/>
          </signal>
        </interface>
        """;

    const string MenuXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg name="parentId" type="i" direction="in"/>
            <arg name="recursionDepth" type="i" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="revision" type="u" direction="out"/>
            <arg name="layout" type="(ia{sv}av)" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="properties" type="a(ia{sv})" direction="out"/>
          </method>
          <method name="Event">
            <arg name="id" type="i" direction="in"/>
            <arg name="eventId" type="s" direction="in"/>
            <arg name="data" type="v" direction="in"/>
            <arg name="timestamp" type="u" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg name="events" type="a(isvu)" direction="in"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg name="id" type="i" direction="in"/>
            <arg name="needUpdate" type="b" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="updatesNeeded" type="ai" direction="out"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg name="updatedProps" type="a(ia{sv})"/>
            <arg name="removedProps" type="a(ias)"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg name="revision" type="u"/>
            <arg name="parent" type="i"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg name="id" type="i"/>
            <arg name="timestamp" type="u"/>
          </signal>
        </interface>
        """;
}
