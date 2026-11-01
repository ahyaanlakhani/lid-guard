using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// The Privacy | Casual switch that sits at the top of both windows. The current mode is the highlighted half.
static class ModeSwitch
{
    public const int Height = 36;

    public static Control[] Create(App app, int x, int y, int width)
    {
        bool casual = Config.CasualMode;
        int half = (width - Ui.S(6)) / 2;
        var privacy = new RButton { Text = "Privacy", Primary = !casual, Font = Ui.F(10.5f, "Semibold") };
        privacy.SetBounds(x, y, half, Ui.S(Height));
        privacy.Click += (a, e) => { if (Config.CasualMode) app.SetMode("privacy"); };
        var casualBtn = new RButton { Text = "Casual", Primary = casual, Font = Ui.F(10.5f, "Semibold") };
        casualBtn.SetBounds(x + half + Ui.S(6), y, half, Ui.S(Height));
        casualBtn.Click += (a, e) => { if (!Config.CasualMode) app.SetMode("casual"); };
        return new Control[] { privacy, casualBtn };
    }

    // moves everything already on a form down to make room for the switch
    public static void MakeRoom(Form form, App app, int x, int y, int width)
    {
        int drop = Ui.S(Height) + Ui.S(14);
        foreach (Control c in form.Controls) c.Top += drop;
        form.Controls.AddRange(Create(app, x, y, width));
    }
}

// A glass that empties as the session runs out, with steam while the PC is kept awake.
class GlassView : Control
{
    public float Level;     // 0 empty .. 1 full
    public bool Awake;

    public GlassView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Ui.PaintParent(this, e.Graphics, InvokePaintBackground);
        var g = e.Graphics;
        var t = Theme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Width, h = Height;
        var r = new RectangleF(w * 0.12f, h * 0.22f, w * 0.76f, h * 0.76f);

        using (var glass = Outline(r))
        {
            g.SetClip(glass);
            float fillTop = r.Bottom - r.Height * Math.Max(0, Math.Min(1, Level));
            if (fillTop < r.Bottom)
            {
                using (var b = new LinearGradientBrush(new RectangleF(r.Left, fillTop, r.Width, r.Bottom - fillTop + 1), Color.FromArgb(235, t.Accent), Color.FromArgb(210, t.AccentDown), 90f))
                    g.FillRectangle(b, r.Left, fillTop, r.Width, r.Bottom - fillTop);
                using (var foam = new Pen(Color.FromArgb(140, 255, 255, 255), 2)) g.DrawLine(foam, r.Left, fillTop, r.Right, fillTop);
            }
            g.ResetClip();
            using (var pen = new Pen(t.Border, 2)) g.DrawPath(pen, glass);
        }

        if (Awake)
        {
            using (var steam = new Pen(Color.FromArgb(120, t.Muted), 2))
            {
                float cx = w / 2;
                g.DrawBezier(steam, cx - 6, h * 0.16f, cx - 14, h * 0.10f, cx + 2, h * 0.06f, cx - 6, 2);
                g.DrawBezier(steam, cx + 8, h * 0.16f, cx + 0, h * 0.10f, cx + 16, h * 0.06f, cx + 8, 2);
            }
        }
    }

    // a glass: straight sides that narrow at the bottom, with rounded corners
    static GraphicsPath Outline(RectangleF r)
    {
        float ins = r.Width * 0.10f, rad = r.Width * 0.20f;
        var p = new GraphicsPath();
        p.AddLine(r.Left, r.Top, r.Right, r.Top);
        p.AddLine(r.Right, r.Top, r.Right - ins, r.Bottom - rad);
        p.AddArc(r.Right - ins - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
        p.AddLine(r.Right - ins - rad, r.Bottom, r.Left + ins + rad, r.Bottom);
        p.AddArc(r.Left + ins, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Casual mode: a proper screen for keeping the PC awake while AI agents work. It shows the time left in a glass,
// which agents are working and for how long, and an activity feed of starts and finishes. Nothing is locked.
// Closing the window keeps LidGuard in the tray.
class CasualForm : Form
{
    readonly App app;
    readonly GlassView glass;
    readonly Label heroTitle, heroSub, quietSub;
    readonly RButton powerBtn;
    readonly Toggle endWhenQuiet, dark, startup;
    readonly Label[] agentNames, agentStates;
    readonly Label[] feedRows;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
    bool toldAboutTray;

    static readonly string[] AgentNames = { "Claude Code", "Codex", "Gemini CLI" };
    static readonly int[] Minutes = { 15, 30, 60, 120, 300, 1440 };
    static readonly string[] PickLabels = { "15m", "30m", "1h", "2h", "5h", "∞" };   // the last is the longest timer (24 h)
    const int DefaultMinutes = 120, FeedRows = 4;

    public CasualForm(App app)
    {
        this.app = app;
        Text = "LidGuard";
        Icon = AppIcon.Full;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.F(10);
        DoubleBuffered = true;
        BackColor = Theme.Current.Bg;

        int pad = Ui.S(20), w = Ui.S(400), y;

        // header
        int logo = Ui.S(36);
        var pic = new PictureBox { Image = new Icon(AppIcon.Full, logo, logo).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        pic.SetBounds(pad, pad, logo, logo);
        var title = Ui.Lbl("LidGuard", Ui.F(15, "Semibold"));
        title.SetBounds(pad + logo + Ui.S(10), pad - Ui.S(1), w - logo, Ui.S(24));
        var tagline = Ui.Lbl("Keep the PC awake while your agents work", Ui.F(9), "muted");
        tagline.SetBounds(pad + logo + Ui.S(10), pad + Ui.S(22), w - logo, Ui.S(18));
        y = pad + logo + Ui.S(16);

        // hero: the glass, what is happening, and the on/off button
        var hero = new Card();
        int heroH = Ui.S(160);
        hero.SetBounds(pad, y, w, heroH);
        glass = new GlassView { Location = new Point(Ui.S(18), Ui.S(14)), Size = new Size(Ui.S(96), Ui.S(140)) };
        heroTitle = Ui.Lbl("Off", Ui.F(20, "Semibold"));
        heroTitle.SetBounds(Ui.S(130), Ui.S(18), w - Ui.S(150), Ui.S(34));
        heroSub = Ui.Lbl("", Ui.F(9.5f), "muted");
        heroSub.SetBounds(Ui.S(130), Ui.S(54), w - Ui.S(150), Ui.S(56));
        powerBtn = new RButton { Primary = true, Text = "Turn on", Font = Ui.F(10.5f, "Semibold") };
        powerBtn.SetBounds(Ui.S(130), heroH - Ui.S(54), Ui.S(140), Ui.S(34));
        powerBtn.Click += (a, e) => PowerClicked();
        hero.Controls.AddRange(new Control[] { glass, heroTitle, heroSub, powerBtn });
        y = hero.Bottom + Ui.S(14);

        // quick picks as pills
        int gap = Ui.S(6), pickW = (w - gap * (Minutes.Length - 1)) / Minutes.Length;
        var picks = new RButton[Minutes.Length];
        for (int i = 0; i < Minutes.Length; i++)
        {
            int minutes = Minutes[i];
            picks[i] = new RButton { Text = PickLabels[i], Font = Ui.F(10, "Semibold") };
            picks[i].SetBounds(pad + i * (pickW + gap), y, pickW, Ui.S(34));
            picks[i].Click += (a, e) => app.StartCasual(minutes, endWhenQuiet.Checked);
        }
        y += Ui.S(34) + Ui.S(18);

        // agents
        var agentsLbl = Section("AI AGENTS", y, pad);
        y = agentsLbl.Bottom + Ui.S(6);
        var agents = new Card();
        int rowH = Ui.S(50);
        agents.SetBounds(pad, y, w, rowH * AgentNames.Length);
        agentNames = new Label[AgentNames.Length];
        agentStates = new Label[AgentNames.Length];
        for (int i = 0; i < AgentNames.Length; i++)
        {
            if (i > 0) agents.Dividers.Add(i * rowH);
            agentNames[i] = Ui.Lbl(AgentNames[i], Ui.F(11));
            agentNames[i].SetBounds(Ui.S(16), i * rowH + Ui.S(8), w - Ui.S(230), Ui.S(22));
            agentStates[i] = Ui.Lbl("Not running", Ui.F(9.5f, "Semibold"), "muted");
            agentStates[i].TextAlign = ContentAlignment.MiddleRight;
            agentStates[i].SetBounds(w - Ui.S(214), i * rowH + Ui.S(8), Ui.S(198), Ui.S(22));
            agents.Controls.Add(agentNames[i]);
            agents.Controls.Add(agentStates[i]);
        }
        y = agents.Bottom + Ui.S(18);

        // activity feed
        var feedLbl = Section("ACTIVITY", y, pad);
        y = feedLbl.Bottom + Ui.S(6);
        var feed = new Card();
        int feedH = Ui.S(12) + Ui.S(21) * FeedRows + Ui.S(8);
        feed.SetBounds(pad, y, w, feedH);
        feedRows = new Label[FeedRows];
        for (int i = 0; i < FeedRows; i++)
        {
            feedRows[i] = Ui.Lbl("", Ui.F(9.5f));
            feedRows[i].SetBounds(Ui.S(16), Ui.S(8) + i * Ui.S(21), w - Ui.S(32), Ui.S(20));
            feed.Controls.Add(feedRows[i]);
        }
        y = feed.Bottom + Ui.S(18);

        // options
        var optionsLbl = Section("OPTIONS", y, pad);
        y = optionsLbl.Bottom + Ui.S(6);
        var options = new Card();
        int setH = Ui.S(54);
        options.SetBounds(pad, y, w, setH * 3);
        endWhenQuiet = new Toggle { Checked = true };
        dark = new Toggle { Checked = Config.Dark };
        startup = new Toggle { Checked = Startup.Enabled };
        quietSub = AddSettingRow(options, 0, setH, "End when agents finish", "", endWhenQuiet);
        AddSettingRow(options, 1, setH, "Dark mode", "Matches the window colours", dark);
        AddSettingRow(options, 2, setH, "Start with Windows", "Stay in the tray after a restart", startup);
        endWhenQuiet.CheckedChanged += (a, e) => UpdateQuietText();
        dark.CheckedChanged += (a, e) => { Config.Dark = dark.Checked; Config.Save(); ApplyTheme(); };
        startup.CheckedChanged += (a, e) => Startup.Set(startup.Checked);
        UpdateQuietText();
        y = options.Bottom + Ui.S(18);

        // footer
        var quit = new RButton { Text = "Quit LidGuard", Font = Ui.F(10) };
        quit.SetBounds(pad, y, w, Ui.S(36));
        quit.Click += (a, e) => app.Quit();
        var foot = Ui.Lbl("Closing this window keeps LidGuard running in the tray.", Ui.F(9), "muted");
        foot.SetBounds(pad, quit.Bottom + Ui.S(10), w + pad * 2, Ui.S(20));
        foot.TextAlign = ContentAlignment.MiddleCenter;

        Controls.AddRange(new Control[] { pic, title, tagline, hero, agentsLbl, agents, feedLbl, feed, optionsLbl, options, quit, foot });
        Controls.AddRange(picks);

        // the Privacy | Casual switch sits above everything else
        ModeSwitch.MakeRoom(this, app, pad, pad, w);
        ClientSize = new Size(w + pad * 2, foot.Bottom + pad);

        // closing the window keeps LidGuard running in the tray
        FormClosing += (a, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing || app.Quitting) return;
            e.Cancel = true;
            Hide();
            if (toldAboutTray) return;
            toldAboutTray = true;
            app.Balloon("Still running in the tray." + (Config.Hotkey != "Off" ? " Press " + Config.Hotkey + " to open." : ""));
        };
        tick.Tick += (a, e) => Refresh();
        tick.Start();
        Disposed += (a, e) => { tick.Dispose(); };

        Ui.Retheme(this);
        Refresh();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.TitleBar(this); }

    Label Section(string text, int y, int pad)
    {
        var l = Ui.Lbl(text, Ui.F(8.5f, "Semibold"), "muted");
        l.SetBounds(pad + Ui.S(4), y, Ui.S(200), Ui.S(18));
        Controls.Add(l);
        return l;
    }

    // one row of the options card: title and muted subtitle on the left, a toggle on the right
    Label AddSettingRow(Card card, int row, int rowH, string title, string sub, Toggle toggle)
    {
        int top = row * rowH;
        if (row > 0) card.Dividers.Add(top);
        var t = Ui.Lbl(title, Ui.F(11));
        t.SetBounds(Ui.S(16), top + Ui.S(9), card.Width - Ui.S(90), Ui.S(22));
        var s = Ui.Lbl(sub, Ui.F(9), "muted");
        s.SetBounds(Ui.S(16), top + Ui.S(30), card.Width - Ui.S(90), Ui.S(18));
        toggle.Location = new Point(card.Width - Ui.S(16) - toggle.Width, top + (rowH - toggle.Height) / 2);
        card.Controls.AddRange(new Control[] { t, s, toggle });
        return s;
    }

    void UpdateQuietText()
    {
        quietSub.Text = endWhenQuiet.Checked ? "Stops after 10 minutes with no agent working" : "Runs until the time is up";
    }

    void PowerClicked()
    {
        if (app.WorkActive) app.StopCasual();
        else app.StartCasual(DefaultMinutes, endWhenQuiet.Checked);
        Refresh();
    }

    void ApplyTheme()
    {
        BackColor = Theme.Current.Bg;
        Ui.Retheme(this);
        Invalidate(true);
    }

    static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

    static int MinutesSince(DateTime since) { return (int)Math.Max(1, Math.Round((DateTime.Now - since).TotalMinutes)); }

    // the hero line: what the PC is doing and why it stays awake
    string HeroText(List<AgentWatch.Status> found, bool on)
    {
        int working = 0;
        foreach (var s in found) if (s.Working) working++;
        if (on && working > 0) return "Working on " + Plural(working, "agent") + ". Staying up until they finish.";
        if (on) return app.WorkStatus + ". Nothing is working right now.";
        return "Turn on, or pick how long below.";
    }

    // keeps the glass, the hero, the agents and the feed current
    void Refresh()
    {
        bool on = app.WorkActive;
        glass.Level = on ? (float)app.WorkRemaining : 0f;
        glass.Awake = on;
        heroTitle.Text = on ? "Awake" : "Off";
        var found = app.AgentStatuses();
        heroSub.Text = HeroText(found, on);
        powerBtn.Text = on ? "Turn off" : "Turn on";

        for (int i = 0; i < AgentNames.Length; i++)
        {
            AgentWatch.Status status = null;
            foreach (var s in found) if (s.Name == AgentNames[i]) status = s;
            if (status == null)
            {
                agentStates[i].Text = "Not running";
                agentStates[i].ForeColor = Theme.Current.Muted;
            }
            else if (status.Working)
            {
                agentStates[i].Text = "Working " + MinutesSince(status.Since) + " min · " + Plural(status.Sessions, "session");
                agentStates[i].ForeColor = Theme.Current.Success;
            }
            else
            {
                agentStates[i].Text = "Idle · " + Plural(status.Sessions, "session");
                agentStates[i].ForeColor = Theme.Current.Muted;
            }
        }

        var events = app.AgentEvents();
        for (int i = 0; i < FeedRows; i++)
        {
            if (events.Count == 0)
            {
                feedRows[i].Text = i == 0 ? "Nothing yet. Starts and finishes show up here." : "";
                feedRows[i].ForeColor = Theme.Current.Muted;
                continue;
            }
            if (i >= events.Count) { feedRows[i].Text = ""; continue; }
            var e = events[i];
            feedRows[i].Text = e.Time.ToString("HH:mm") + "   " + (e.Started
                ? e.Name + " started working"
                : e.Name + " finished · worked " + Math.Max(1, (int)Math.Round(e.Worked.TotalMinutes)) + " min");
            feedRows[i].ForeColor = e.Started ? Theme.Current.Accent : Theme.Current.Success;
        }
    }
}
