// LidGuard UI: theme, custom-drawn controls, lock screen, dialogs and the main window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ---------- theme ----------
class Theme
{
    public Color Bg, Surface, Field, FieldHover, Border, TrackOff, Fg, Muted, Accent, AccentHover, AccentDown, Danger, Success;

    public static readonly Theme DarkTheme = new Theme
    {
        Bg = C(12, 13, 17), Surface = C(21, 23, 29), Field = C(29, 32, 40), FieldHover = C(37, 41, 51), Border = C(46, 50, 62), TrackOff = C(66, 71, 86),
        Fg = C(236, 238, 245), Muted = C(139, 145, 163), Accent = C(99, 140, 255), AccentHover = C(124, 160, 255), AccentDown = C(80, 118, 235),
        Danger = C(255, 112, 112), Success = C(74, 222, 128)
    };

    public static readonly Theme LightTheme = new Theme
    {
        Bg = C(242, 244, 248), Surface = C(255, 255, 255), Field = C(247, 248, 251), FieldHover = C(237, 240, 245), Border = C(218, 222, 230), TrackOff = C(196, 202, 214),
        Fg = C(20, 22, 29), Muted = C(98, 105, 122), Accent = C(37, 99, 235), AccentHover = C(29, 85, 216), AccentDown = C(24, 72, 190),
        Danger = C(214, 40, 40), Success = C(22, 163, 74)
    };

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    public static Theme Current { get { return Config.Dark ? DarkTheme : LightTheme; } }
}

// ---------- helpers ----------
static class Ui
{
    public static readonly float Scale = ReadScale();

    static float ReadScale()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
    }

    // layout is written in 96-dpi pixels; S() scales it for the actual display
    public static int S(float px) { return (int)Math.Round(px * Scale); }

    public static Font F(float pt, string weight = null) { return new Font(weight == null ? "Segoe UI" : "Segoe UI " + weight, pt); }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // paints the parent's background behind a control so rounded corners blend in;
    // the caller passes its own InvokePaintBackground because that method is protected
    public static void PaintParent(Control c, Graphics g, Action<Control, PaintEventArgs> invoke)
    {
        if (c.Parent == null) return;
        var state = g.Save();
        g.TranslateTransform(-c.Left, -c.Top);
        using (var pe = new PaintEventArgs(g, c.Bounds)) invoke(c.Parent, pe);
        g.Restore(state);
    }

    public static Label Lbl(string text, Font font, string tag = null)
    {
        return new Label { Text = text, Font = font, Tag = tag, AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false };
    }

    // dark or light title bar that matches the window (Windows 10 20H1+ / 11)
    public static void TitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        var t = Theme.Current;
        int dark = Config.Dark ? 1 : 0, caption = ColorRef(t.Bg), text = ColorRef(t.Fg);
        Native.DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);
        Native.DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
        Native.DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
    }

    static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

    // re-colour a window after a theme change; labels take their colour from Tag
    public static void Retheme(Control root)
    {
        var t = Theme.Current;
        foreach (Control c in root.Controls)
        {
            var tag = c.Tag as string;
            var link = c as LinkLabel;
            if (link != null) { link.LinkColor = t.Accent; link.ActiveLinkColor = t.AccentHover; }
            else if (c is Label) c.ForeColor = tag == "muted" ? t.Muted : tag == "danger" ? t.Danger : t.Fg;
            var box = c as RBox;
            if (box != null) box.Apply();
            if (c is Card) c.BackColor = t.Surface;
            if (c is ComboBox) { c.BackColor = t.Field; c.ForeColor = t.Fg; }
            c.Invalidate();
            Retheme(c);
        }
    }
}

// ---------- controls ----------
class RButton : Control, IButtonControl
{
    public bool Primary, Chevron;   // Chevron: dropdown look, text left + arrow right
    bool hover, down;
    public DialogResult DialogResult { get; set; }

    public RButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Font = Ui.F(10);
        AccessibleRole = AccessibleRole.PushButton;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var f = FindForm();
        if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Enter || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) PerformClick(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Primary ? (down ? t.AccentDown : hover ? t.AccentHover : t.Accent)
                             : (down ? t.Border : hover ? t.FieldHover : t.Field);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Ui.Round(r, Ui.S(10)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (!Primary) using (var pen = new Pen(t.Border)) g.DrawPath(pen, p);
            if (Focused && ShowFocusCues) using (var pen = new Pen(t.AccentHover, Math.Max(1.5f, Ui.Scale * 1.5f))) g.DrawPath(pen, p);
        }
        Color fg = Primary ? Color.White : t.Fg;
        if (!Chevron)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        var tr = new Rectangle(Ui.S(12), 0, Width - Ui.S(36), Height);
        TextRenderer.DrawText(g, Text, Font, tr, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        float cx = Width - Ui.S(18), cy = Height / 2f, s = Ui.S(4);
        using (var pen = new Pen(t.Muted, Math.Max(1.5f, Ui.Scale * 1.5f)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
        }
    }
}

// themed colours for menus (tray menu, shortcut picker)
class MenuColors : ProfessionalColorTable
{
    static Theme T { get { return Theme.Current; } }
    public override Color ToolStripDropDownBackground { get { return T.Surface; } }
    public override Color ImageMarginGradientBegin { get { return T.Surface; } }
    public override Color ImageMarginGradientMiddle { get { return T.Surface; } }
    public override Color ImageMarginGradientEnd { get { return T.Surface; } }
    public override Color MenuBorder { get { return T.Border; } }
    public override Color MenuItemBorder { get { return T.FieldHover; } }
    public override Color MenuItemSelected { get { return T.FieldHover; } }
    public override Color SeparatorDark { get { return T.Border; } }
    public override Color SeparatorLight { get { return T.Surface; } }
    public override Color CheckBackground { get { return T.Surface; } }
    public override Color CheckSelectedBackground { get { return T.FieldHover; } }
    public override Color CheckPressedBackground { get { return T.FieldHover; } }
}

class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Current.Fg : Theme.Current.Muted;
        base.OnRenderItemText(e);
    }

    // the stock check mark is black; draw an accent tick instead
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        float s = r.Height * 0.22f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.Current.Accent, Math.Max(1.8f, Ui.Scale * 1.8f)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            e.Graphics.DrawLines(pen, new[] { new PointF(cx - s * 1.4f, cy), new PointF(cx - s * 0.3f, cy + s), new PointF(cx + s * 1.5f, cy - s * 1.1f) });
        }
    }

    public static ContextMenuStrip NewMenu()
    {
        return new ContextMenuStrip { Renderer = new MenuRenderer(), Font = Ui.F(10), Padding = new Padding(0, Ui.S(4), 0, Ui.S(4)) };
    }
}

// rounded text field: a borderless TextBox inside a painted frame, with placeholder text
class RBox : Panel
{
    public readonly TextBox Box;
    readonly string cue;

    public RBox(bool password, string placeholder, float pt = 11f)
    {
        cue = placeholder;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Box = new TextBox { BorderStyle = BorderStyle.None, UseSystemPasswordChar = password, Font = Ui.F(pt), AccessibleName = placeholder };
        Controls.Add(Box);
        Box.GotFocus += (a, e) => Invalidate();
        Box.LostFocus += (a, e) => Invalidate();
        Box.HandleCreated += (a, e) => { if (cue != null) Native.SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue); };
        Click += (a, e) => Box.Focus();
        Cursor = Cursors.IBeam;
        Height = Ui.S(44);
        Apply();
    }

    public string Value { get { return Box.Text; } }
    public void Clear() { Box.Clear(); }

    public void Apply()
    {
        var t = Theme.Current;
        BackColor = t.Field;
        Box.BackColor = t.Field;
        Box.ForeColor = t.Fg;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Ui.S(14);
        Box.SetBounds(pad, (Height - Box.Height) / 2, Width - pad * 2, Box.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool focus = Box.Focused;
        float w = focus ? Math.Max(1.5f, 1.5f * Ui.Scale) : 1f;
        using (var p = Ui.Round(new RectangleF(w / 2, w / 2, Width - w, Height - w), Ui.S(10)))
        using (var b = new SolidBrush(t.Field))
        using (var pen = new Pen(focus ? t.Accent : t.Border, w))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
    }
}

class Toggle : CheckBox
{
    public Toggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Size = new Size(Ui.S(44), Ui.S(24));
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Ui.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(Checked ? t.Accent : t.TrackOff)) g.FillPath(b, p);
            if (Focused && ShowFocusCues) using (var pen = new Pen(t.AccentHover, Math.Max(1.5f, Ui.Scale * 1.5f))) g.DrawPath(pen, p);
        }
        float k = Height - Ui.S(6), x = Checked ? Width - k - Ui.S(3) : Ui.S(3);
        g.FillEllipse(Brushes.White, x, Ui.S(3), k, k);
    }
}

// rounded surface that groups rows of settings
class Card : Panel
{
    public readonly List<int> Dividers = new List<int>();

    public Card()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Current.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.S(14)))
        using (var b = new SolidBrush(t.Surface))
        using (var pen = new Pen(t.Border))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
            foreach (int y in Dividers) g.DrawLine(pen, Ui.S(16), y, Width - Ui.S(16), y);
        }
    }
}

// small pill with a coloured dot: "success", "accent" or "danger"
class Chip : Control
{
    public string Kind;

    public Chip(string text, string kind)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Ui.F(9.5f);
        Fit();
    }

    public void Set(string text, string kind) { Text = text; Kind = kind; Fit(); Invalidate(); }

    void Fit()
    {
        Size = new Size(TextRenderer.MeasureText(Text, Font).Width + Ui.S(38), Ui.S(30));
        AccessibleName = Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f))
        using (var b = new SolidBrush(t.Surface))
        using (var pen = new Pen(t.Border))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        Color dot = Kind == "danger" ? t.Danger : Kind == "accent" ? t.Accent : t.Success;
        float d = Ui.S(8);
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, Ui.S(13), (Height - d) / 2, d, d);
        var tr = new Rectangle(Ui.S(28), 0, Width - Ui.S(36), Height);
        TextRenderer.DrawText(g, Text, Font, tr, t.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

class Avatar : Control
{
    public Avatar(string letter)
    {
        Text = letter;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Ui.F(24, "Semibold");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        Ui.PaintParent(this, g, InvokePaintBackground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var b = new LinearGradientBrush(r, t.AccentHover, t.AccentDown, 45f)) g.FillEllipse(b, r);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// ---------- dialogs ----------
class Dialog : Form
{
    protected readonly int Pad = Ui.S(24), W = Ui.S(320);
    protected int Y;
    readonly Label err;

    public Dialog(string title, string intro)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowIcon = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.F(10);
        DoubleBuffered = true;
        BackColor = Theme.Current.Bg;
        Y = Pad;
        if (intro != null)
        {
            var f = Ui.F(9.5f);
            int h = TextRenderer.MeasureText(intro, f, new Size(W, 0), TextFormatFlags.WordBreak).Height;
            var l = Ui.Lbl(intro, f, "muted");
            l.SetBounds(Pad, Y, W, h);
            Controls.Add(l);
            Y += h + Ui.S(16);
        }
        err = Ui.Lbl("", Ui.F(9.5f), "danger");
    }

    protected RBox Field(string label, string placeholder)
    {
        var l = Ui.Lbl(label, Ui.F(9.5f, "Semibold"));
        l.SetBounds(Pad, Y, W, Ui.S(20));
        var b = new RBox(true, placeholder);
        b.SetBounds(Pad, Y + Ui.S(24), W, Ui.S(42));
        Controls.Add(l);
        Controls.Add(b);
        Y += Ui.S(78);
        return b;
    }

    protected void Finish(string okText, string cancelText, Action onOk)
    {
        err.SetBounds(Pad, Y - Ui.S(6), W, Ui.S(22));
        Controls.Add(err);
        int bw = (W - Ui.S(10)) / 2;
        var cancel = new RButton { Text = cancelText, DialogResult = DialogResult.Cancel };
        cancel.SetBounds(Pad, Y + Ui.S(22), bw, Ui.S(40));
        var ok = new RButton { Text = okText, Primary = true, Font = Ui.F(10, "Semibold") };
        ok.SetBounds(Pad + bw + Ui.S(10), Y + Ui.S(22), bw, Ui.S(40));
        ok.Click += (a, e) => onOk();
        Controls.Add(cancel);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        ClientSize = new Size(W + Pad * 2, ok.Bottom + Pad);
        Ui.Retheme(this);
    }

    protected void Fail(string message) { err.Text = message; }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.TitleBar(this); }
}

class PasswordDialog : Dialog
{
    public PasswordDialog(bool needCurrent)
        : base(needCurrent ? "Change password" : "Set a password", needCurrent ? null : "This password unlocks LidGuard's lock screen.")
    {
        RBox cur = needCurrent ? Field("Current password", "Current password") : null;
        RBox nw = Field("New password", "At least 4 characters");
        RBox cf = Field("Confirm password", "Type it again");
        Finish("Save", "Cancel", () =>
        {
            if (cur != null && !Config.Verify(cur.Value)) { Fail("Current password is wrong."); return; }
            if (nw.Value.Length < 4) { Fail("Use at least 4 characters."); return; }
            if (nw.Value != cf.Value) { Fail("Passwords do not match."); return; }
            Config.SetPassword(nw.Value);
            DialogResult = DialogResult.OK;
        });
    }
}

class KeyDialog : Dialog
{
    public KeyDialog(bool firstRun)
        : base("Secret key", "After 5 wrong passwords, your secret key unlocks LidGuard. Make it different from your password. Only a hash is stored, so it can't be shown again.")
    {
        RBox key = Field("Secret key", "At least 6 characters");
        RBox cf = Field("Confirm secret key", "Type it again");
        Finish("Save key", firstRun ? "Skip for now" : "Cancel", () =>
        {
            if (key.Value.Length < 6) { Fail("Use at least 6 characters."); return; }
            if (key.Value != cf.Value) { Fail("Keys do not match."); return; }
            if (Config.Verify(key.Value)) { Fail("Must be different from your password."); return; }
            Config.SetKey(key.Value);
            DialogResult = DialogResult.OK;
        });
    }
}

// asks for the current password before a sensitive change
class ConfirmDialog : Dialog
{
    public ConfirmDialog() : base("Confirm it's you", "Enter your current password to continue.")
    {
        RBox pw = Field("Password", "Current password");
        Finish("Continue", "Cancel", () =>
        {
            if (Config.Verify(pw.Value)) DialogResult = DialogResult.OK;
            else { Fail("Wrong password."); pw.Clear(); }
        });
    }
}

// ---------- main window ----------
class MainForm : Form
{
    readonly App app;
    readonly RButton hotkey;
    readonly ContextMenuStrip hotkeyMenu;
    readonly Toggle dark, startup;
    readonly Chip status;
    readonly Label keySub;
    readonly RButton keyBtn;
    readonly Label citySub, wallSub;
    readonly RButton wallBtn;
    bool toldAboutTray;

    public MainForm(App app)
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
        int pad = Ui.S(24), w = Ui.S(400);

        // header
        int logo = Ui.S(44);
        var pic = new PictureBox { Image = new Icon(AppIcon.Full, logo, logo).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        pic.SetBounds(pad, pad, logo, logo);
        var title = Ui.Lbl("LidGuard", Ui.F(17, "Semibold"));
        title.SetBounds(pad + logo + Ui.S(12), pad - Ui.S(3), Ui.S(300), Ui.S(30));
        var tagline = Ui.Lbl("Cover the desktop. Keep the PC running.", Ui.F(9.5f), "muted");
        tagline.SetBounds(pad + logo + Ui.S(12), pad + Ui.S(26), Ui.S(320), Ui.S(20));

        var lockBtn = new RButton { Text = "Privacy cover", Primary = true, Font = Ui.F(12, "Semibold") };
        lockBtn.SetBounds(pad, pad + Ui.S(70), Ui.S(195), Ui.S(52));
        var secureBtn = new RButton { Text = "Lock Windows", Font = Ui.F(11, "Semibold") };
        secureBtn.SetBounds(pad + Ui.S(205), pad + Ui.S(70), Ui.S(195), Ui.S(52));
        secureBtn.Click += (a, e) => app.SecureLock();
        Controls.Add(secureBtn);
        status = new Chip("", "success");
        status.Location = new Point(pad, lockBtn.Bottom + Ui.S(12));

        var workBtn = new RButton { Text = "Secure closed-lid session...", Font = Ui.F(10, "Semibold") };
        workBtn.SetBounds(pad, status.Bottom + Ui.S(12), w, Ui.S(38));
        workBtn.Click += (a, e) => app.StartWork();
        var workStatus = Ui.Lbl(app.WorkStatus, Ui.F(9), "muted");
        workStatus.SetBounds(pad, workBtn.Bottom + Ui.S(6), w, Ui.S(22));
        var workTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        workTimer.Tick += (a, e) =>
        {
            workStatus.Text = app.WorkStatus;
            workBtn.Text = app.WorkActive ? "End secure work session" : "Secure closed-lid session...";
        };
        workTimer.Start();
        Disposed += (a, e) => workTimer.Dispose();
        Controls.AddRange(new Control[] { workBtn, workStatus });

        // settings
        var settingsLbl = Section("SETTINGS", workStatus.Bottom + Ui.S(16), pad);
        var settings = new Card();
        settings.SetBounds(pad, settingsLbl.Bottom + Ui.S(8), w, Ui.S(62) * 3);
        hotkey = new RButton { Chevron = true, Text = Config.Hotkey, Size = new Size(Ui.S(140), Ui.S(34)) };
        hotkeyMenu = MenuRenderer.NewMenu();
        foreach (var name in Hotkeys.Names)
        {
            var item = new ToolStripMenuItem(name) { Padding = new Padding(0, Ui.S(3), 0, Ui.S(3)) };
            string picked = name;
            item.Click += (a, e) => SetHotkey(picked);
            hotkeyMenu.Items.Add(item);
        }
        hotkey.Click += (a, e) =>
        {
            foreach (ToolStripMenuItem item in hotkeyMenu.Items) item.Checked = item.Text == Config.Hotkey;
            hotkeyMenu.MinimumSize = new Size(hotkey.Width, 0);
            hotkeyMenu.Show(hotkey, new Point(0, hotkey.Height + Ui.S(4)));
        };
        dark = new Toggle { Checked = Config.Dark };
        startup = new Toggle { Checked = Startup.Enabled };
        Row(settings, 0, "Lock shortcut", "Works from any app", hotkey);
        Row(settings, 1, "Dark mode", "Also used on the lock screen", dark);
        Row(settings, 2, "Start with Windows", "Keeps the shortcut ready after a restart", startup);

        // security
        var securityLbl = Section("SECURITY", settings.Bottom + Ui.S(22), pad);
        var security = new Card();
        security.SetBounds(pad, securityLbl.Bottom + Ui.S(8), w, Ui.S(62) * 2);
        var pwBtn = new RButton { Text = "Change", Size = new Size(Ui.S(96), Ui.S(34)) };
        keyBtn = new RButton { Size = new Size(Ui.S(96), Ui.S(34)) };
        Row(security, 0, "Password", "Unlocks the lock screen", pwBtn);
        keySub = Row(security, 1, "Secret key", "", keyBtn);

        // lock screen personalisation
        var lockLbl = Section("LOCK SCREEN", security.Bottom + Ui.S(22), pad);
        var personal = new Card();
        personal.SetBounds(pad, lockLbl.Bottom + Ui.S(8), w, Ui.S(62) * 4);
        var nameBox = Input(Config.Name, "Your name");
        var cityBox = Input(Config.City, "City");
        var noteBox = Input(Config.Note, "Back soon");
        Row(personal, 0, "Your name", "Greeting and login name", nameBox);
        citySub = Row(personal, 1, "Weather city", Config.HasPlace ? "Weather for the widget" : "Not set", cityBox);
        Row(personal, 2, "Away note", "Shown while you're away", noteBox);
        wallBtn = new RButton { Chevron = true, Text = "Built-in", Size = new Size(Ui.S(170), Ui.S(34)) };
        wallSub = Row(personal, 3, "Wallpaper", "", wallBtn);
        var wallMenu = MenuRenderer.NewMenu();
        wallMenu.Items.Add("Choose a video...", null, (a, e) => ChooseVideo());
        var builtIn = new ToolStripMenuItem("Built-in");
        foreach (var name in Wallpaper.Styles)
        {
            string style = name;
            var item = new ToolStripMenuItem(style, null, (a, e) => { Config.Wallpaper = "builtin"; Config.WallStyle = style; Config.Video = ""; Config.VideoStart = Config.VideoEnd = 0; Config.VideoSections = ""; Config.Save(); UpdateWallpaper(); });
            builtIn.DropDownItems.Add(item);
        }
        wallMenu.Items.Add(builtIn);
        wallMenu.Opening += (a, e) =>
        {
            foreach (ToolStripMenuItem item in builtIn.DropDownItems)
                item.Checked = Config.WallpaperMode == "builtin" && item.Text == Config.WallStyle;
        };
        wallBtn.Click += (a, e) => wallMenu.Show(wallBtn, new Point(0, wallBtn.Height + Ui.S(4)));
        UpdateWallpaper();
        nameBox.Box.TextChanged += (a, e) => { Config.Name = nameBox.Value.Trim(); Config.Save(); };
        noteBox.Box.TextChanged += (a, e) => { Config.Note = noteBox.Value.Trim(); Config.Save(); };
        cityBox.Box.Leave += (a, e) => LookUpCity(cityBox);
        cityBox.Box.KeyDown += (a, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LookUpCity(cityBox); } };

        var foot = Ui.Lbl("Closing this window keeps LidGuard running in the tray.", Ui.F(9), "muted");
        foot.SetBounds(pad, personal.Bottom + Ui.S(16), w, Ui.S(20));
        foot.TextAlign = ContentAlignment.MiddleCenter;

        Controls.AddRange(new Control[] { pic, title, tagline, lockBtn, status, settings, security, personal, foot });
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, foot.Bottom + pad);
        ClientSize = new Size(w + pad * 2 + SystemInformation.VerticalScrollBarWidth,
            Math.Min(foot.Bottom + pad, Screen.FromControl(this).WorkingArea.Height - Ui.S(80)));

        dark.CheckedChanged += (a, e) => { Config.Dark = dark.Checked; Config.Save(); ApplyTheme(); };
        startup.CheckedChanged += (a, e) => Startup.Set(startup.Checked);
        lockBtn.Click += (a, e) => app.Lock();
        pwBtn.Click += (a, e) => { using (var d = new PasswordDialog(true)) d.ShowDialog(this); };
        keyBtn.Click += (a, e) =>
        {
            if (Config.HasKey) using (var c = new ConfirmDialog()) if (c.ShowDialog(this) != DialogResult.OK) return;
            using (var d = new KeyDialog(false)) d.ShowDialog(this);
            UpdateSecurity();
        };
        UpdateStatus();
        UpdateSecurity();
        Ui.Retheme(this);

        // closing the window keeps LidGuard running in the tray so the shortcut still works
        FormClosing += (a, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing || app.Quitting) return;
            e.Cancel = true;
            Hide();
            if (toldAboutTray) return;
            toldAboutTray = true;
            app.Balloon("Still running in the tray." + (Config.Hotkey != "Off" ? " Press " + Config.Hotkey + " to lock." : ""));
        };


    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.TitleBar(this); }

    Label Section(string text, int y, int pad)
    {
        var l = Ui.Lbl(text, Ui.F(8.5f, "Semibold"), "muted");
        l.SetBounds(pad + Ui.S(4), y, Ui.S(200), Ui.S(18));
        Controls.Add(l);
        return l;
    }

    // one settings row: title + muted subtitle on the left, a control on the right; returns the subtitle
    Label Row(Card card, int i, string title, string sub, Control right)
    {
        int h = Ui.S(62), y = i * h, pad = Ui.S(16);
        if (i > 0) card.Dividers.Add(y);
        right.Location = new Point(card.Width - pad - right.Width, y + (h - right.Height) / 2);
        int textW = right.Left - pad - Ui.S(12);   // labels stop short of the control so they never paint over it
        var t1 = Ui.Lbl(title, Ui.F(10.5f));
        t1.SetBounds(pad, y + Ui.S(11), textW, Ui.S(22));
        var t2 = Ui.Lbl(sub, Ui.F(9f), "muted");
        t2.SetBounds(pad, y + Ui.S(33), textW, Ui.S(18));
        t2.AutoEllipsis = true;
        card.Controls.Add(right);
        card.Controls.Add(t1);
        card.Controls.Add(t2);
        return t2;
    }

    void SetHotkey(string name)
    {
        Config.Hotkey = name;
        Config.Save();
        hotkey.Text = name;
        app.ApplyHotkey(true);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (Config.Hotkey == "Off") status.Set("Ready. Use the button to lock", "accent");
        else if (app.HotkeyOk) status.Set("Ready. " + Config.Hotkey + " locks from any app", "success");
        else status.Set(Config.Hotkey + " is taken by another app", "danger");
    }

    // copy the chosen clip next to the settings so it keeps working if the original moves
    void ChooseVideo()
    {
        using (var d = new OpenFileDialog { Title = "Choose a wallpaper video", Filter = "Videos (*.mp4;*.mov;*.wmv;*.m4v)|*.mp4;*.mov;*.wmv;*.m4v|All files|*.*" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string dest = System.IO.Path.Combine(Config.Dir, "wallpaper" + System.IO.Path.GetExtension(d.FileName).ToLowerInvariant());
                System.IO.Directory.CreateDirectory(Config.Dir);
                System.IO.File.Copy(d.FileName, dest, true);
                Config.Video = dest;
                Config.VideoStart = Config.VideoEnd = 0;     // a new clip loops in full
                Config.VideoSections = "";
                Config.Wallpaper = "video";
                Config.Save();
                Loop.EnsureAsync();
            }
            catch (Exception ex) { MessageBox.Show("Couldn't use that video: " + ex.Message, "LidGuard"); }
        }
        UpdateWallpaper();
    }

    void UpdateWallpaper()
    {
        bool hasVideo = Config.WallpaperMode == "video" && Config.Video.Length > 0 && System.IO.File.Exists(Config.Video);
        wallBtn.Text = hasVideo ? "Video" : Config.WallStyle;
        wallSub.Text = !hasVideo ? "Built-in, follows the light or dark theme"
            : VideoWallpaper.ParseSections(Config.VideoSections).Count > 1 ? "Seamless loop of your road shots" + (Config.VideoSpeed != 1 ? ", " + Config.VideoSpeed.ToString("0.##") + "x speed" : "")
            : Config.VideoEnd > Config.VideoStart ? "Loops " + Config.VideoStart.ToString("0.#") + "s to " + Config.VideoEnd.ToString("0.#") + "s, pauses with the lid shut"
            : "Loops, pauses with the lid shut";
        wallSub.Invalidate();
    }

    static RBox Input(string value, string placeholder)
    {
        var b = new RBox(false, placeholder, 10f) { Size = new Size(Ui.S(170), Ui.S(36)) };
        b.Box.Text = value;
        return b;
    }

    // turn the typed city into coordinates for the weather widget
    void LookUpCity(RBox box)
    {
        string q = box.Value.Trim();
        if (q.Length == 0 || q == Config.City) return;
        citySub.Text = "Looking up...";
        citySub.Tag = "muted";
        Ui.Retheme(this);
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            double lat, lon;
            string found = null;
            try { found = Weather.Geocode(q, out lat, out lon); }
            catch { lat = lon = 0; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (found == null) { citySub.Text = "Couldn't find that city"; citySub.Tag = "danger"; }
                    else
                    {
                        Config.City = found; Config.Lat = lat; Config.Lon = lon; Config.Save();
                        box.Box.Text = found;
                        citySub.Text = "Weather for the widget";
                        citySub.Tag = "muted";
                        Weather.Current = null;
                        Weather.Refresh();
                    }
                    Ui.Retheme(this);
                }));
            }
            catch { }
        });
    }

    void UpdateSecurity()
    {
        keyBtn.Text = Config.HasKey ? "Change" : "Set up";
        keySub.Text = Config.HasKey ? "Unlocks after 5 wrong passwords" : "Not set. Recommended";
        keySub.Tag = Config.HasKey ? "muted" : "danger";
        Ui.Retheme(this);
    }

    void ApplyTheme()
    {
        BackColor = Theme.Current.Bg;
        Ui.Retheme(this);
        Ui.TitleBar(this);
        Invalidate(true);
    }
}
