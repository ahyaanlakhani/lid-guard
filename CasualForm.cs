using System;
using System.Collections.Generic;
using System.Drawing;
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

// Casual mode: keep the PC awake for a while, or while AI agents work. Nothing is locked and there is no
// password. It shows which agents are running, how long each has been working, and an activity feed of
// starts and finishes. Closing the window keeps LidGuard in the tray.
class CasualForm : Form
{
    readonly App app;
    readonly Label statusTitle, statusSub, quietSub;
    readonly RButton powerBtn;
    readonly Toggle endWhenQuiet, dark, startup;
    readonly Label[] agentNames, agentStates, feedRows;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 2000 };
    bool toldAboutTray;

    static readonly string[] AgentNames = { "Claude Code", "Codex", "Gemini CLI" };
    static readonly int[] Minutes = { 15, 30, 60, 120, 300, 1440 };
    static readonly string[] PickLabels = { "15m", "30m", "1h", "2h", "5h", "∞" };   // the last is the longest timer (24 h)
    const int DefaultMinutes = 120, FeedRows = 8;

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
        int logo = Ui.S(40);
        var pic = new PictureBox { Image = new Icon(AppIcon.Full, logo, logo).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        pic.SetBounds(pad, pad, logo, logo);
        var title = Ui.Lbl("LidGuard", Ui.F(16, "Semibold"));
        title.SetBounds(pad + logo + Ui.S(12), pad - Ui.S(2), w - logo, Ui.S(26));
        var tagline = Ui.Lbl("Keep the PC awake while your agents work", Ui.F(9.5f), "muted");
        tagline.SetBounds(pad + logo + Ui.S(12), pad + Ui.S(24), w - logo, Ui.S(20));

        // status card: whether the PC is being kept awake, and the main button
        y = pad + logo + Ui.S(18);
        var statusCard = new Card();
        statusCard.SetBounds(pad, y, w, Ui.S(150));
        statusTitle = Ui.Lbl("Off", Ui.F(22, "Semibold"));
        statusTitle.SetBounds(Ui.S(20), Ui.S(16), w - Ui.S(40), Ui.S(36));
        statusSub = Ui.Lbl("", Ui.F(9.5f), "muted");
        statusSub.SetBounds(Ui.S(20), Ui.S(54), w - Ui.S(40), Ui.S(20));
        powerBtn = new RButton { Primary = true, Text = "Turn on", Font = Ui.F(11, "Semibold") };
        powerBtn.SetBounds(Ui.S(20), Ui.S(92), Ui.S(180), Ui.S(38));
        powerBtn.Click += (a, e) => PowerClicked();
        statusCard.Controls.AddRange(new Control[] { statusTitle, statusSub, powerBtn });
        y = statusCard.Bottom + Ui.S(14);

        // quick picks
        int gap = Ui.S(8), pickW = (w - gap * (Minutes.Length - 1)) / Minutes.Length;
        var picks = new RButton[Minutes.Length];
        for (int i = 0; i < Minutes.Length; i++)
        {
            int minutes = Minutes[i];
            picks[i] = new RButton { Text = PickLabels[i], Font = Ui.F(10, "Semibold") };
            picks[i].SetBounds(pad + i * (pickW + gap), y, pickW, Ui.S(36));
            picks[i].Click += (a, e) => app.StartCasual(minutes, endWhenQuiet.Checked);
        }
        y += Ui.S(36) + Ui.S(22);

        // agents: each kind shows whether it is running, working (and for how long), or idle
        var agentsLbl = Section("AI AGENTS", y, pad);
        y = agentsLbl.Bottom + Ui.S(6);
        var agents = new Card();
        int rowH = Ui.S(56);
        agents.SetBounds(pad, y, w, rowH * AgentNames.Length);
        agentNames = new Label[AgentNames.Length];
        agentStates = new Label[AgentNames.Length];
        for (int i = 0; i < AgentNames.Length; i++)
        {
            if (i > 0) agents.Dividers.Add(i * rowH);
            agentNames[i] = Ui.Lbl(AgentNames[i], Ui.F(11));
            agentNames[i].SetBounds(Ui.S(16), i * rowH + Ui.S(9), w - Ui.S(32), Ui.S(22));
            agentStates[i] = Ui.Lbl("Not running", Ui.F(9), "muted");
            agentStates[i].SetBounds(Ui.S(16), i * rowH + Ui.S(31), w - Ui.S(32), Ui.S(18));
            agents.Controls.Add(agentNames[i]);
            agents.Controls.Add(agentStates[i]);
        }
        y = agents.Bottom + Ui.S(22);

        // activity feed: starts and finishes, newest first
        var feedLbl = Section("ACTIVITY", y, pad);
        y = feedLbl.Bottom + Ui.S(6);
        var feed = new Card();
        int feedH = Ui.S(14) + Ui.S(22) * FeedRows;
        feed.SetBounds(pad, y, w, feedH);
        feedRows = new Label[FeedRows];
        for (int i = 0; i < FeedRows; i++)
        {
            feedRows[i] = Ui.Lbl("", Ui.F(9.5f));
            feedRows[i].SetBounds(Ui.S(16), Ui.S(8) + i * Ui.S(22), w - Ui.S(32), Ui.S(20));
            feed.Controls.Add(feedRows[i]);
        }
        y = feed.Bottom + Ui.S(22);

        // settings
        var settingsLbl = Section("SETTINGS", y, pad);
        y = settingsLbl.Bottom + Ui.S(6);
        var settings = new Card();
        int setH = Ui.S(58);
        settings.SetBounds(pad, y, w, setH * 3);
        endWhenQuiet = new Toggle { Checked = true };
        dark = new Toggle { Checked = Config.Dark };
        startup = new Toggle { Checked = Startup.Enabled };
        quietSub = AddSettingRow(settings, 0, setH, "End when agents finish", "", endWhenQuiet);
        AddSettingRow(settings, 1, setH, "Dark mode", "Matches the window colours", dark);
        AddSettingRow(settings, 2, setH, "Start with Windows", "Stay in the tray after a restart", startup);
        endWhenQuiet.CheckedChanged += (a, e) => UpdateQuietText();
        dark.CheckedChanged += (a, e) => { Config.Dark = dark.Checked; Config.Save(); ApplyTheme(); };
        startup.CheckedChanged += (a, e) => Startup.Set(startup.Checked);
        UpdateQuietText();
        y = settings.Bottom + Ui.S(16);

        var foot = Ui.Lbl("Closing this window keeps LidGuard running in the tray.", Ui.F(9), "muted");
        foot.SetBounds(pad, y, w + pad * 2, Ui.S(20));
        foot.TextAlign = ContentAlignment.MiddleCenter;

        Controls.AddRange(new Control[] { pic, title, tagline, statusCard, agentsLbl, agents, feedLbl, feed, settingsLbl, settings, foot });
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

    // one row of a settings card: title and muted subtitle on the left, a toggle on the right
    Label AddSettingRow(Card card, int row, int rowH, string title, string sub, Toggle toggle)
    {
        int top = row * rowH;
        if (row > 0) card.Dividers.Add(top);
        var t = Ui.Lbl(title, Ui.F(11));
        t.SetBounds(Ui.S(16), top + Ui.S(10), card.Width - Ui.S(90), Ui.S(22));
        var s = Ui.Lbl(sub, Ui.F(9), "muted");
        s.SetBounds(Ui.S(16), top + Ui.S(32), card.Width - Ui.S(90), Ui.S(18));
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

    // keeps the status, the agents and the feed current while the window is open
    void Refresh()
    {
        bool on = app.WorkActive;
        statusTitle.Text = on ? "Awake" : "Off";
        statusSub.Text = on ? app.WorkStatus : "Turn on, or pick how long below";
        powerBtn.Text = on ? "Turn off" : "Turn on";

        var found = app.AgentStatuses();
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
                agentStates[i].Text = "Working for " + MinutesSince(status.Since) + " min · " + Plural(status.Sessions, "session");
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
