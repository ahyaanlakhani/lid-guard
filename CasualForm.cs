using System;
using System.Drawing;
using System.Windows.Forms;

// Casual mode: keep the PC awake for a while, or while AI agents work. Nothing is locked and there is no
// password. Closing the window keeps LidGuard in the tray, like the privacy window.
class CasualForm : Form
{
    readonly App app;
    readonly Label statusTitle, statusSub, quietSub;
    readonly RButton powerBtn;
    readonly Toggle endWhenQuiet, dark, startup;
    readonly Label[] agentNames, agentStates;
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
    bool toldAboutTray;

    static readonly string[] AgentNames = { "Claude Code", "Codex", "Gemini CLI" };
    static readonly int[] Minutes = { 15, 30, 60, 120, 300, 1440 };
    static readonly string[] PickLabels = { "15m", "30m", "1h", "2h", "5h", "∞" };   // the last one is "until I turn it off" (the longest timer)
    const int DefaultMinutes = 120;

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

        // status card: what is happening right now, and the main button
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

        // agents: each one that is installed shows running / working / idle
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
            agentNames[i].SetBounds(Ui.S(16), i * rowH + Ui.S(9), w - Ui.S(150), Ui.S(22));
            agentStates[i] = Ui.Lbl("Not running", Ui.F(9), "muted");
            agentStates[i].SetBounds(Ui.S(16), i * rowH + Ui.S(31), w - Ui.S(32), Ui.S(18));
            agents.Controls.Add(agentNames[i]);
            agents.Controls.Add(agentStates[i]);
        }
        y = agents.Bottom + Ui.S(22);

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

        var privacyBtn = new RButton { Text = "Switch to privacy mode...", Font = Ui.F(10) };
        privacyBtn.SetBounds(pad, y, w, Ui.S(36));
        privacyBtn.Click += (a, e) => app.SetMode("privacy");
        y = privacyBtn.Bottom + Ui.S(14);

        var foot = Ui.Lbl("Closing this window keeps LidGuard running in the tray.", Ui.F(9), "muted");
        foot.SetBounds(pad, y, w + pad * 2, Ui.S(20));
        foot.TextAlign = ContentAlignment.MiddleCenter;

        Controls.AddRange(new Control[] { pic, title, tagline, statusCard, agentsLbl, agents, settingsLbl, settings, privacyBtn, foot });
        Controls.AddRange(picks);
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
        VisibleChanged += (a, e) => app.WatchAgentsForWindow(Visible);
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

    // keeps the status and the agent list current while the window is open
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
            else
            {
                agentStates[i].Text = (status.Working ? "Working" : "Idle") + " · " + status.Sessions + (status.Sessions == 1 ? " session" : " sessions");
                agentStates[i].ForeColor = status.Working ? Theme.Current.Success : Theme.Current.Muted;
            }
        }
    }
}
