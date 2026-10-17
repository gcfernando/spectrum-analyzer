// Developer :: > Gehan Fernando
#region Imports

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Spectrum;

#endregion Imports
#region RoundRectangle

internal static class RoundRectangle
{
    public static GraphicsPath RoundRect(Rectangle Rectangle, int Curve)
    {
        var P = new GraphicsPath();
        var ArcRectangleWidth = Curve * 2;
        P.AddArc(new Rectangle(Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -180, 90);
        P.AddArc(new Rectangle(Rectangle.Width - ArcRectangleWidth + Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -90, 90);
        P.AddArc(new Rectangle(Rectangle.Width - ArcRectangleWidth + Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), 0, 90);
        P.AddArc(new Rectangle(Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), 90, 90);
        P.AddLine(new Point(Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y), new Point(Rectangle.X, Curve + Rectangle.Y));
        return P;
    }
    public static GraphicsPath RoundRect(int X, int Y, int Width, int Height, int Curve)
    {
        var Rectangle = new Rectangle(X, Y, Width, Height);
        var P = new GraphicsPath();
        var ArcRectangleWidth = Curve * 2;
        P.AddArc(new Rectangle(Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -180, 90);
        P.AddArc(new Rectangle(Rectangle.Width - ArcRectangleWidth + Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -90, 90);
        P.AddArc(new Rectangle(Rectangle.Width - ArcRectangleWidth + Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), 0, 90);
        P.AddArc(new Rectangle(Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), 90, 90);
        P.AddLine(new Point(Rectangle.X, Rectangle.Height - ArcRectangleWidth + Rectangle.Y), new Point(Rectangle.X, Curve + Rectangle.Y));
        return P;
    }
    public static GraphicsPath RoundedTopRect(Rectangle Rectangle, int Curve)
    {
        var P = new GraphicsPath();
        var ArcRectangleWidth = Curve * 2;
        P.AddArc(new Rectangle(Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -180, 90);
        P.AddArc(new Rectangle(Rectangle.Width - ArcRectangleWidth + Rectangle.X, Rectangle.Y, ArcRectangleWidth, ArcRectangleWidth), -90, 90);
        P.AddLine(new Point(Rectangle.X + Rectangle.Width, Rectangle.Y + ArcRectangleWidth), new Point(Rectangle.X + Rectangle.Width, Rectangle.Y + Rectangle.Height - 1));
        P.AddLine(new Point(Rectangle.X, Rectangle.Height - 1 + Rectangle.Y), new Point(Rectangle.X, Rectangle.Y + Curve));
        return P;
    }
}

#endregion RoundRectangle
#region ThemeContainer

public class Ambiance_ThemeContainer : ContainerControl
{
    public enum MouseState
    {
        None = 0,
        Over = 1,
        Down = 2,
        Block = 3
    }

    private Rectangle HeaderRect;
    protected MouseState State;
    private readonly int MoveHeight;
    private Point MouseP = new(0, 0);
    private bool Cap = false;
    private bool HasShown;

    public bool Sizable { get; set; } = true;

    public bool SmartBounds { get; set; } = true;

    public bool RoundCorners
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = true;

    protected bool IsParentForm { get; private set; }

    protected bool IsParentMdi => Parent != null && Parent.Parent != null;

    protected bool ControlMode
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    }

    private FormStartPosition _StartPosition = FormStartPosition.CenterScreen;
    public FormStartPosition StartPosition
    {
        get => IsParentForm && !ControlMode ? ParentForm.StartPosition : _StartPosition;
        set
        {
            _StartPosition = value;

            if (IsParentForm && !ControlMode)
            {
                ParentForm.StartPosition = value;
            }
        }
    }

    protected sealed override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);

        if (Parent == null)
        {
            return;
        }
        IsParentForm = Parent is Form;

        if (!ControlMode)
        {
            InitializeMessages();

            if (IsParentForm)
            {
                this.ParentForm.FormBorderStyle = FormBorderStyle.None;
                this.ParentForm.TransparencyKey = Color.Fuchsia;

                if (!DesignMode)
                {
                    ParentForm.Shown += FormShown;
                }
            }
            Parent.BackColor = BackColor;
            Parent.MinimumSize = new Size(261, 65);
        }
    }

    protected sealed override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!ControlMode)
        {
            HeaderRect = new Rectangle(0, 0, Width - 14, MoveHeight - 7);
        }
        Invalidate();
    }

    protected override void OnMouseDown(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        SetState(MouseState.Down);
        if (!((IsParentForm && ParentForm.WindowState == FormWindowState.Maximized) || ControlMode))
        {
            if (HeaderRect.Contains(e.Location))
            {
                Capture = false;
                WM_LMBUTTONDOWN = true;
                DefWndProc(ref Messages[0]);
            }
            else if (Sizable && !(Previous == 0))
            {
                Capture = false;
                WM_LMBUTTONDOWN = true;
                DefWndProc(ref Messages[Previous]);
            }
        }
    }

    protected override void OnMouseUp(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Cap = false;
    }

    protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!(IsParentForm && ParentForm.WindowState == FormWindowState.Maximized))
        {
            if (Sizable && !ControlMode)
            {
                InvalidateMouse();
            }
        }
        if (Cap)
        {
            Parent.Location = (System.Drawing.Point)(object)(System.Convert.ToDouble(MousePosition) - System.Convert.ToDouble(MouseP));
        }
    }

    protected override void OnInvalidated(System.Windows.Forms.InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        ParentForm?.Text = Text;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => base.OnPaintBackground(e);

    protected override void OnTextChanged(System.EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    private void FormShown(object sender, EventArgs e)
    {
        if (ControlMode || HasShown)
        {
            return;
        }

        if (_StartPosition is FormStartPosition.CenterParent or FormStartPosition.CenterScreen)
        {
            var SB = Screen.PrimaryScreen.Bounds;
            var CB = ParentForm.Bounds;
            ParentForm.Location = new Point((SB.Width / 2) - (CB.Width / 2), (SB.Height / 2) - (CB.Height / 2));
        }
        HasShown = true;
    }

    private void SetState(MouseState current)
    {
        State = current;
        Invalidate();
    }

    private Point GetIndexPoint;
    private bool B1x;
    private bool B2x;
    private bool B3;
    private bool B4;
    private int GetIndex()
    {
        GetIndexPoint = PointToClient(MousePosition);
        B1x = GetIndexPoint.X < 7;
        B2x = GetIndexPoint.X > Width - 7;
        B3 = GetIndexPoint.Y < 7;
        B4 = GetIndexPoint.Y > Height - 7;

        return B1x && B3 ? 4 : B1x && B4 ? 7 : B2x && B3 ? 5 : B2x && B4 ? 8 : B1x ? 1 : B2x ? 2 : B3 ? 3 : B4 ? 6 : 0;
    }

    private int Current;
    private int Previous;
    private void InvalidateMouse()
    {
        Current = GetIndex();
        if (Current == Previous)
        {
            return;
        }

        Previous = Current;
        switch (Previous)
        {
            case 0:
                Cursor = Cursors.Default;
                break;
            case 6:
                Cursor = Cursors.SizeNS;
                break;
            case 8:
                Cursor = Cursors.SizeNWSE;
                break;
            case 7:
                Cursor = Cursors.SizeNESW;
                break;
        }
    }

    private readonly Message[] Messages = new Message[9];
    private void InitializeMessages()
    {
        Messages[0] = Message.Create(Parent.Handle, 161, new IntPtr(2), IntPtr.Zero);
        for (var I = 1; I <= 8; I++)
        {
            Messages[I] = Message.Create(Parent.Handle, 161, new IntPtr(I + 9), IntPtr.Zero);
        }
    }

    internal void CorrectBounds(Rectangle bounds)
    {
        if (Parent.Width > bounds.Width)
        {
            Parent.Width = bounds.Width;
        }
        if (Parent.Height > bounds.Height)
        {
            Parent.Height = bounds.Height;
        }

        Parent.Location = new Point(
            Math.Max(bounds.Left, Math.Min(Parent.Left, bounds.Right - Parent.Width)),
            Math.Max(bounds.Top, Math.Min(Parent.Top, bounds.Bottom - Parent.Height)));
    }

    private bool WM_LMBUTTONDOWN;
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (WM_LMBUTTONDOWN && m.Msg == 513)
        {
            WM_LMBUTTONDOWN = false;

            SetState(MouseState.Over);
            if (!SmartBounds)
            {
                return;
            }

            if (IsParentMdi)
            {
                CorrectBounds(new Rectangle(Point.Empty, Parent.Parent.Size));
            }
            else
            {
                CorrectBounds(Screen.FromControl(Parent).WorkingArea);
            }
        }
    }

    private readonly Pen _outerBorderPen = new(Color.FromArgb(30, 28, 25));
    private readonly SolidBrush _separatorBrush = new(Color.FromArgb(82, 75, 60));
    private readonly SolidBrush _titleBrush = new(Color.FromArgb(215, 210, 196));
    private readonly Font _titleFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private readonly StringFormat _titleFormat = new()
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private LinearGradientBrush _headerBrush;
    private int _headerBrushWidth;

    private LinearGradientBrush _bodyGradientBrush;
    private Size _bodyGradientSize;
    private Color _headerStart = Color.FromArgb(68, 64, 54);
    private Color _headerEnd = Color.FromArgb(38, 35, 29);
    private Color _bodyStart = Color.FromArgb(18, 18, 18);
    private Color _bodyEnd = Color.FromArgb(8, 8, 8);

    // header buttons eat into the title area, so the title stops at this x
    public int TitleRightInset { get; set; }

    internal void ApplyTheme(VisualTheme theme)
    {
        BackColor = theme.Canvas;
        _headerStart = theme.HeaderStart;
        _headerEnd = theme.HeaderEnd;
        _bodyStart = theme.Surface;
        _bodyEnd = theme.Canvas;
        _outerBorderPen.Color = theme.Frame;
        _separatorBrush.Color = theme.Divider;
        _titleBrush.Color = theme.PrimaryText;
        _headerBrush?.Dispose();
        _headerBrush = null;
        _bodyGradientBrush?.Dispose();
        _bodyGradientBrush = null;
        foreach (Control control in Controls)
        {
            if (control is Ambiance_ControlBox controlBox)
            {
                controlBox.ApplyTheme(theme);
            }
        }

        Invalidate();
    }

    protected override void CreateHandle() => base.CreateHandle();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _outerBorderPen.Dispose();
            _separatorBrush.Dispose();
            _titleBrush.Dispose();
            _titleFont.Dispose();
            _titleFormat.Dispose();
            _headerBrush?.Dispose();
            _bodyGradientBrush?.Dispose();
        }
        base.Dispose(disposing);
    }

    public Ambiance_ThemeContainer()
    {
        SetStyle((ControlStyles)139270, true);
        BackColor = Color.Black;
        Padding = new Padding(20, 56, 20, 16);
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        MoveHeight = 48;
        Font = new Font("Segoe UI", 9);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var G = e.Graphics;
        G.Clear(BackColor);

        if (_headerBrush == null || _headerBrushWidth != Width)
        {
            _headerBrush?.Dispose();
            _headerBrush = new LinearGradientBrush(
                new Rectangle(1, 1, Math.Max(1, Width - 2), 36),
                _headerStart,
                _headerEnd,
                LinearGradientMode.Vertical);
            _headerBrushWidth = Width;
        }

        G.DrawRectangle(_outerBorderPen, 0, 0, Width - 1, Height - 1);
        G.FillRectangle(_headerBrush, 1, 1, Width - 2, 36);

        var bodyRect = new Rectangle(1, 37, Math.Max(1, Width - 2), Math.Max(0, Height - 47));
        if (bodyRect.Width > 0 && bodyRect.Height > 0)
        {
            if (_bodyGradientBrush == null || _bodyGradientSize != bodyRect.Size)
            {
                _bodyGradientBrush?.Dispose();
                _bodyGradientBrush = new LinearGradientBrush(
                    bodyRect,
                    _bodyStart,
                    _bodyEnd,
                    LinearGradientMode.Vertical);
                _bodyGradientSize = bodyRect.Size;
            }
            G.FillRectangle(_bodyGradientBrush, bodyRect);
        }

        G.FillRectangle(_separatorBrush, 1, 37, Width - 2, 1);

        if (RoundCorners)
        {
            G.FillRectangle(Brushes.Fuchsia, 0, 0, 4, 1);
            G.FillRectangle(Brushes.Fuchsia, 0, 1, 1, 3);
            G.FillRectangle(Brushes.Fuchsia, 1, 1, 1, 1);
            G.FillRectangle(Brushes.Black, 1, 2, 1, 2);
            G.FillRectangle(Brushes.Black, 2, 1, 2, 1);

            G.FillRectangle(Brushes.Fuchsia, Width - 4, 0, 4, 1);
            G.FillRectangle(Brushes.Fuchsia, Width - 1, 1, 1, 3);
            G.FillRectangle(Brushes.Fuchsia, Width - 2, 1, 1, 1);
            G.FillRectangle(Brushes.Black, Width - 2, 2, 1, 2);
            G.FillRectangle(Brushes.Black, Width - 4, 1, 2, 1);

            G.FillRectangle(Brushes.Fuchsia, 0, Height - 4, 1, 4);
            G.FillRectangle(Brushes.Fuchsia, 1, Height - 1, 3, 1);
            G.FillRectangle(Brushes.Fuchsia, 1, Height - 2, 1, 1);
            G.FillRectangle(Brushes.Black, 1, Height - 4, 1, 2);
            G.FillRectangle(Brushes.Black, 2, Height - 2, 2, 1);

            G.FillRectangle(Brushes.Fuchsia, Width - 1, Height - 4, 1, 4);
            G.FillRectangle(Brushes.Fuchsia, Width - 4, Height - 1, 3, 1);
            G.FillRectangle(Brushes.Fuchsia, Width - 2, Height - 2, 1, 1);
            G.FillRectangle(Brushes.Black, Width - 2, Height - 4, 1, 2);
            G.FillRectangle(Brushes.Black, Width - 4, Height - 2, 2, 1);
        }

        var titleRight = TitleRightInset > 0 ? Math.Min(TitleRightInset, Width - 12) : Width - 12;
        var titleBounds = new Rectangle(16, 4, Math.Max(0, titleRight - 16), 28);
        if (titleBounds.Width > 0 && !string.IsNullOrEmpty(Text))
        {
            G.DrawString(Text, _titleFont, _titleBrush, titleBounds, _titleFormat);
        }
    }
}

#endregion ThemeContainer
#region ControlBox

public class Ambiance_ControlBox : Control
{
    public enum MouseState
    {
        None = 0,
        Over = 1,
        Down = 2
    }

    private readonly Button _closeButton;
    private readonly Button _minimizeButton;
    private readonly Button _maximizeButton;
    private readonly ToolTip _toolTip = new();
    private VisualTheme _theme;

    public bool EnableMaximize
    {
        get;
        set
        {
            field = value;
            if (_maximizeButton != null)
            {
                _maximizeButton.Visible = value;
                _minimizeButton.Left = 0;
                _maximizeButton.Left = 22;
                _closeButton.Left = value ? 44 : 22;
            }
            Size = field ? new Size(64, 22) : new Size(44, 22);
            Invalidate();
        }
    } = true;

    public Ambiance_ControlBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.DoubleBuffer, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleName = "Window controls";
        Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeButton = CreateWindowButton("closeButton", "Close", 44, 2);
        _minimizeButton = CreateWindowButton("minimizeButton", "Minimize", 0, 0);
        _maximizeButton = CreateWindowButton("maximizeButton", "Maximize or restore", 22, 1);
        _closeButton.Click += (sender, args) => FindForm()?.Close();
        _minimizeButton.Click += (sender, args) =>
        {
            var form = FindForm();
            form?.WindowState = FormWindowState.Minimized;
        };
        _maximizeButton.Click += (sender, args) =>
        {
            var form = FindForm();
            if (form != null && EnableMaximize)
            {
                form.WindowState = form.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
            }
        };
        Controls.AddRange(new Control[] { _minimizeButton, _maximizeButton, _closeButton });
        ApplyTheme(BarColorThemes.ClassicSmooth.Ui);
        Size = new Size(64, 22);
    }

    private Button CreateWindowButton(string name, string accessibleName, int left, int tabIndex)
    {
        var button = new Button
        {
            Name = name,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.PushButton,
            Location = new Point(left, 0),
            Size = new Size(20, 22),
            TabIndex = tabIndex,
            TabStop = true,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 1;
        button.Paint += PaintWindowButton;
        button.GotFocus += (sender, args) => button.Invalidate();
        button.LostFocus += (sender, args) => button.Invalidate();
        _toolTip.SetToolTip(button, accessibleName);
        return button;
    }

    internal void ApplyTheme(VisualTheme theme)
    {
        _theme = theme;
        foreach (Button button in Controls)
        {
            button.BackColor = theme.ControlSurface;
            button.ForeColor = theme.PrimaryText;
            button.FlatAppearance.BorderColor = theme.Divider;
            button.FlatAppearance.MouseOverBackColor = theme.Hover;
            button.FlatAppearance.MouseDownBackColor = theme.Selection;
            button.Invalidate();
        }
    }

    private void PaintWindowButton(object sender, PaintEventArgs e)
    {
        var button = (Button)sender;
        var centerX = button.ClientSize.Width / 2;
        var centerY = button.ClientSize.Height / 2;
        var radius = Math.Max(3, Math.Min(button.Width, button.Height) / 5);
        using var pen = new Pen(button.ForeColor, Math.Max(1f, e.Graphics.DpiX / 96f));
        if (button == _closeButton)
        {
            e.Graphics.DrawLine(pen, centerX - radius, centerY - radius, centerX + radius, centerY + radius);
            e.Graphics.DrawLine(pen, centerX - radius, centerY + radius, centerX + radius, centerY - radius);
        }
        else if (button == _minimizeButton)
        {
            e.Graphics.DrawLine(pen, centerX - radius, centerY + (radius / 2), centerX + radius, centerY + (radius / 2));
        }
        else
        {
            e.Graphics.DrawRectangle(pen, centerX - radius, centerY - radius, radius * 2, radius * 2);
        }

        if (button.Focused)
        {
            using var focusPen = new Pen(_theme.Focus) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawRectangle(focusPen, 2, 2, Math.Max(1, button.Width - 5), Math.Max(1, button.Height - 5));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Size = EnableMaximize ? new Size(64, 22) : new Size(44, 22);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Location = Parent == null
            ? new Point(5, 8)
            : new Point(Math.Max(5, Parent.ClientSize.Width - Width - 8), 8);
    }
}

#endregion ControlBox
#region Separator

public class Ambiance_Separator : Control
{
    public Ambiance_Separator()
    {
        SetStyle(ControlStyles.ResizeRedraw, true);
        this.Size = new Size(120, 10);
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawLine(new Pen(Color.FromArgb(224, 222, 220)), 0, 5, Width, 5);
        e.Graphics.DrawLine(new Pen(Color.FromArgb(250, 249, 249)), 0, 6, Width, 6);
    }
}

#endregion Separator
#region ProgressBar

public class Ambiance_ProgressBar : Control
{
    #region Enums

    public enum Alignment
    {
        Right,
        Center
    }

    #endregion Enums
    #region Variables

    private int _Maximum = 100;
    private int _Value = 0;
    private bool _DrawHatch;

    private bool _ShowPercentage;
    private GraphicsPath GP1;
    private GraphicsPath GP2;
    private GraphicsPath GP3;
    private Rectangle R1;
    private Rectangle R2;
    private LinearGradientBrush GB1;
    private LinearGradientBrush GB2;
    private int I1;

    #endregion Variables
    #region Properties

    public int Maximum
    {
        get => _Maximum;
        set
        {
            if (value < 1)
            {
                value = 1;
            }

            if (value < _Value)
            {
                _Value = value;
            }

            _Maximum = value;
            Invalidate();
        }
    }

    public int Minimum
    {
        get;
        set
        {
            field = value;

            if (value > _Maximum)
            {
                _Maximum = value;
            }

            if (value > _Value)
            {
                _Value = value;
            }

            Invalidate();
        }
    }

    public int Value
    {
        get => _Value;
        set
        {
            if (value > _Maximum)
            {
                value = Maximum;
            }

            _Value = value;
            Invalidate();
        }
    }

    public Alignment ValueAlignment
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    }

    public bool DrawHatch
    {
        get => _DrawHatch;
        set
        {
            _DrawHatch = value;
            Invalidate();
        }
    }

    public bool ShowPercentage
    {
        get => _ShowPercentage;
        set
        {
            _ShowPercentage = value;
            Invalidate();
        }
    }

    #endregion Properties
    #region EventArgs

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        this.Height = 20;
        var minimumSize = new Size(58, 20);
        this.MinimumSize = minimumSize;
    }

    #endregion EventArgs

    public Ambiance_ProgressBar()
    {
        _Maximum = 100;
        _ShowPercentage = true;
        _DrawHatch = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    public void Increment(int value)
    {
        this._Value += value;
        Invalidate();
    }

    public void Deincrement(int value)
    {
        this._Value -= value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var B = new Bitmap(Width, Height);
        var G = Graphics.FromImage(B);

        G.Clear(Color.Transparent);
        G.SmoothingMode = SmoothingMode.HighQuality;

        GP1 = RoundRectangle.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 4);
        GP2 = RoundRectangle.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), 4);

        R1 = new Rectangle(0, 2, Width - 1, Height - 1);
        GB1 = new LinearGradientBrush(R1, Color.FromArgb(255, 255, 255), Color.FromArgb(230, 230, 230), 90f);

        G.FillRectangle(new SolidBrush(Color.FromArgb(244, 241, 243)), R1);
        G.SetClip(GP1);
        G.FillPath(new SolidBrush(Color.FromArgb(244, 241, 243)), RoundRectangle.RoundRect(new Rectangle(1, 1, Width - 3, (Height / 2) - 2), 4));

        I1 = (int)Math.Round((this._Value - Minimum) / (double)(this._Maximum - Minimum) * (this.Width - 3));
        if (I1 > 1)
        {
            GP3 = RoundRectangle.RoundRect(new Rectangle(1, 1, I1, Height - 3), 4);

            R2 = new Rectangle(1, 1, I1, Height - 3);
            GB2 = new LinearGradientBrush(R2, Color.FromArgb(214, 89, 37), Color.FromArgb(223, 118, 75), 90f);

            G.FillPath(GB2, GP3);

            if (_DrawHatch == true)
            {
                for (var i = 0; i <= (Width - 1) * _Maximum / _Value; i += 20)
                {
                    G.DrawLine(new Pen(new SolidBrush(Color.FromArgb(25, Color.White)), 10.0F), new Point(System.Convert.ToInt32(i), 0), new Point(i - 10, Height));
                }
            }

            G.SetClip(GP3);
            G.SmoothingMode = SmoothingMode.None;
            G.SmoothingMode = SmoothingMode.AntiAlias;
            G.ResetClip();
        }

        var DrawString = Convert.ToString(Convert.ToInt32(Value)) + "%";
        var textX = (int)(this.Width - G.MeasureString(DrawString, Font).Width - 1);
        var textY = (this.Height / 2) - (System.Convert.ToInt32(G.MeasureString(DrawString, Font).Height / 2) - 2);

        if (_ShowPercentage == true)
        {
            switch (ValueAlignment)
            {
                case Alignment.Right:
                    G.DrawString(DrawString, new Font("Segoe UI", 8), Brushes.DimGray, new Point(textX, textY));
                    break;
                case Alignment.Center:
                    G.DrawString(DrawString, new Font("Segoe UI", 8), Brushes.DimGray, new Rectangle(0, 0, Width, Height + 2), new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    });
                    break;
            }
        }

        G.DrawPath(new Pen(Color.FromArgb(180, 180, 180)), GP2);

        e.Graphics.DrawImage((Image)B.Clone(), 0, 0);
        G.Dispose();
        B.Dispose();
    }
}

#endregion ProgressBar
#region Toggle Button

[DefaultEvent("ToggledChanged")]
public class Ambiance_Toggle : Control
{
    #region Enums

    public enum _Type
    {
        OnOff,
        YesNo,
        IO
    }

    #endregion Enums
    #region Variables

    public delegate void ToggledChangedEventHandler();
    private ToggledChangedEventHandler ToggledChangedEvent;

    public event ToggledChangedEventHandler ToggledChanged
    {
        add => ToggledChangedEvent = (ToggledChangedEventHandler)System.Delegate.Combine(ToggledChangedEvent, value); remove => ToggledChangedEvent = (ToggledChangedEventHandler)System.Delegate.Remove(ToggledChangedEvent, value);
    }

    private Rectangle Bar;
    private Size cHandle = new(15, 20);

    #endregion Variables
    #region Properties

    public bool Toggled
    {
        get;
        set
        {
            field = value;
            Invalidate();
            ToggledChangedEvent?.Invoke();
        }
    }

    public _Type Type
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    }

    #endregion Properties
    #region EventArgs

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = 79;
        Height = 27;
    }

    protected override void OnMouseUp(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Toggled = !Toggled;
        _ = Focus();
    }

    #endregion EventArgs

    public Ambiance_Toggle() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var G = e.Graphics;

        G.SmoothingMode = SmoothingMode.HighQuality;
        G.Clear(Parent.BackColor);

        var ControlRectangle = new Rectangle(0, 0, Width - 1, Height - 1);
        var ControlPath = RoundRectangle.RoundRect(ControlRectangle, 4);

        int SwitchXLoc;

        LinearGradientBrush BackgroundLGB;
        if (Toggled)
        {
            SwitchXLoc = 37;
            BackgroundLGB = new LinearGradientBrush(ControlRectangle, Color.FromArgb(231, 108, 58), Color.FromArgb(236, 113, 63), 90.0F);
        }
        else
        {
            SwitchXLoc = 0;
            BackgroundLGB = new LinearGradientBrush(ControlRectangle, Color.FromArgb(208, 208, 208), Color.FromArgb(226, 226, 226), 90.0F);
        }

        G.FillPath(BackgroundLGB, ControlPath);

        switch (Type)
        {
            case _Type.OnOff:
                if (Toggled)
                {
                    G.DrawString("ON", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.WhiteSmoke, Bar.X + 18, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                else
                {
                    G.DrawString("OFF", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.DimGray, Bar.X + 59, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                break;
            case _Type.YesNo:
                if (Toggled)
                {
                    G.DrawString("YES", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.WhiteSmoke, Bar.X + 18, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                else
                {
                    G.DrawString("NO", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.DimGray, Bar.X + 59, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                break;
            case _Type.IO:
                if (Toggled)
                {
                    G.DrawString("I", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.WhiteSmoke, Bar.X + 18, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                else
                {
                    G.DrawString("O", new Font("Segoe UI", 12, FontStyle.Regular), Brushes.DimGray, Bar.X + 59, (float)(Bar.Y + 13.5), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                break;
        }

        var SwitchRectangle = new Rectangle(SwitchXLoc, 0, Width - 38, Height);
        var SwitchPath = RoundRectangle.RoundRect(SwitchRectangle, 4);
        var SwitchButtonLGB = new LinearGradientBrush(SwitchRectangle, Color.FromArgb(253, 253, 253), Color.FromArgb(240, 238, 237), LinearGradientMode.Vertical);

        G.FillPath(SwitchButtonLGB, SwitchPath);

        if (Toggled == true)
        {
            G.DrawPath(new Pen(Color.FromArgb(185, 89, 55)), SwitchPath);
            G.DrawPath(new Pen(Color.FromArgb(185, 89, 55)), ControlPath);
        }
        else
        {
            G.DrawPath(new Pen(Color.FromArgb(181, 181, 181)), SwitchPath);
            G.DrawPath(new Pen(Color.FromArgb(181, 181, 181)), ControlPath);
        }
    }
}

#endregion Toggle Button
#region ComboBox

public class Ambiance_ComboBox : ComboBox
{
    #region Variables

    #endregion Variables
    #region Custom Properties

    public int StartIndex
    {
        get;
        set
        {
            field = value;
            try
            {
                base.SelectedIndex = value;
            }
            catch
            {
            }
            Invalidate();
        }
    } = 0;

    public Color HoverSelectionColor
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    }

    #endregion Custom Properties
    #region EventArgs

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        base.OnDrawItem(e);
        var LGB = new LinearGradientBrush(e.Bounds, Color.FromArgb(246, 132, 85), Color.FromArgb(231, 108, 57), 90.0F);

        if (System.Convert.ToInt32(e.State & DrawItemState.Selected) == (int)DrawItemState.Selected)
        {
            if (!(e.Index == -1))
            {
                e.Graphics.FillRectangle(LGB, e.Bounds);
                e.Graphics.DrawString(GetItemText(Items[e.Index]), e.Font, Brushes.WhiteSmoke, e.Bounds);
            }
        }
        else
        {
            if (!(e.Index == -1))
            {
                e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(242, 241, 240)), e.Bounds);
                e.Graphics.DrawString(GetItemText(Items[e.Index]), e.Font, Brushes.DimGray, e.Bounds);
            }
        }
        LGB.Dispose();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        SuspendLayout();
        Update();
        ResumeLayout();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => base.OnPaintBackground(e);

    protected override void OnResize(EventArgs e) => base.OnResize(e);

    #endregion EventArgs

    public Ambiance_ComboBox()
    {
        SetStyle((ControlStyles)139286, true);
        SetStyle(ControlStyles.Selectable, false);

        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;

        BackColor = Color.FromArgb(246, 246, 246);
        ForeColor = Color.FromArgb(142, 142, 142);
        Size = new Size(135, 26);
        ItemHeight = 20;
        DropDownHeight = 100;
        Font = new Font("Segoe UI", 10, FontStyle.Regular);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.Clear(Parent.BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var GP = RoundRectangle.RoundRect(0, 0, Width - 1, Height - 1, 5);
        var LGB = new LinearGradientBrush(ClientRectangle, Color.FromArgb(253, 252, 252), Color.FromArgb(239, 237, 236), 90.0F);

        e.Graphics.SetClip(GP);
        e.Graphics.FillRectangle(LGB, ClientRectangle);
        e.Graphics.ResetClip();

        e.Graphics.DrawPath(new Pen(Color.FromArgb(180, 180, 180)), GP);
        e.Graphics.DrawString(Text, Font, new SolidBrush(Color.FromArgb(76, 76, 97)), new Rectangle(3, 0, Width - 20, Height), new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        });
        e.Graphics.DrawString("6", new Font("Marlett", 13, FontStyle.Regular), new SolidBrush(Color.FromArgb(119, 119, 118)), new Rectangle(3, 0, Width - 4, Height), new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Far
        });
        e.Graphics.DrawLine(new Pen(Color.FromArgb(224, 222, 220)), Width - 24, 4, Width - 24, this.Height - 5);
        e.Graphics.DrawLine(new Pen(Color.FromArgb(250, 249, 249)), Width - 25, 4, Width - 25, this.Height - 5);

        GP.Dispose();
        LGB.Dispose();
    }
}

#endregion ComboBox
#region NumericUpDown

public class Ambiance_NumericUpDown : Control
{
    #region Enums

    public enum _TextAlignment
    {
        Near,
        Center
    }

    #endregion Enums
    #region Variables

    private GraphicsPath Shape;
    private readonly Pen P1;

    private long _Value;
    private long _Minimum;
    private long _Maximum;
    private int Xval;
    private bool KeyboardNum;
    private readonly Timer LongPressTimer = new();

    #endregion Variables
    #region Properties

    public long Value
    {
        get => _Value;
        set
        {
            if (value <= _Maximum & value >= _Minimum)
            {
                _Value = value;
            }
            Invalidate();
        }
    }

    public long Minimum
    {
        get => _Minimum;
        set
        {
            if (value < _Maximum)
            {
                _Minimum = value;
            }
            if (_Value < _Minimum)
            {
                _Value = Minimum;
            }
            Invalidate();
        }
    }

    public long Maximum
    {
        get => _Maximum;
        set
        {
            if (value > _Minimum)
            {
                _Maximum = value;
            }
            if (_Value > _Maximum)
            {
                _Value = _Maximum;
            }
            Invalidate();
        }
    }

    public _TextAlignment TextAlignment
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    }

    #endregion Properties
    #region EventArgs

    protected override void OnResize(System.EventArgs e)
    {
        base.OnResize(e);
        Height = 28;
        MinimumSize = new Size(93, 28);
        Shape = new GraphicsPath();
        Shape.AddArc(0, 0, 10, 10, 180, 90);
        Shape.AddArc(Width - 11, 0, 10, 10, -90, 90);
        Shape.AddArc(Width - 11, Height - 11, 10, 10, 0, 90);
        Shape.AddArc(0, Height - 11, 10, 10, 90, 90);
        Shape.CloseAllFigures();
    }

    protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Xval = e.Location.X;
        Invalidate();

        Cursor = e.X < Width - 50 ? Cursors.IBeam : Cursors.Default;
        if (e.X > this.Width - 25 && e.X < this.Width - 10)
        {
            Cursor = Cursors.Hand;
        }
        if (e.X > this.Width - 44 && e.X < this.Width - 33)
        {
            Cursor = Cursors.Hand;
        }
    }

    private void ClickButton()
    {
        if (Xval > this.Width - 25 && Xval < this.Width - 10)
        {
            if ((Value + 1) <= _Maximum)
            {
                _Value++;
            }
        }
        else
        {
            if (Xval > this.Width - 44 && Xval < this.Width - 33)
            {
                if ((Value - 1) >= _Minimum)
                {
                    _Value--;
                }
            }
            KeyboardNum = !KeyboardNum;
        }
        _ = Focus();
        Invalidate();
    }

    protected override void OnMouseDown(System.Windows.Forms.MouseEventArgs e)
    {
        base.OnMouseClick(e);
        ClickButton();
        LongPressTimer.Start();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        LongPressTimer.Stop();
    }
    private void LongPressTimer_Tick(object sender, EventArgs e) => ClickButton();
    protected override void OnKeyPress(System.Windows.Forms.KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        try
        {
            if (KeyboardNum == true)
            {
                _Value = long.Parse(_Value.ToString() + e.KeyChar.ToString().ToString());
            }
            if (_Value > _Maximum)
            {
                _Value = _Maximum;
            }
        }
        catch (Exception)
        {
        }
    }

    protected override void OnKeyUp(System.Windows.Forms.KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Back)
        {
            var TemporaryValue = _Value.ToString();
            TemporaryValue = TemporaryValue.Remove(Convert.ToInt32(TemporaryValue.Length - 1));
            if (TemporaryValue.Length == 0)
            {
                TemporaryValue = "0";
            }
            _Value = Convert.ToInt32(TemporaryValue);
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta > 0)
        {
            if ((Value + 1) <= _Maximum)
            {
                _Value++;
            }
            Invalidate();
        }
        else
        {
            if ((Value - 1) >= _Minimum)
            {
                _Value--;
            }
            Invalidate();
        }
    }

    #endregion EventArgs

    public Ambiance_NumericUpDown()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.UserPaint, true);

        P1 = new Pen(Color.FromArgb(180, 180, 180));
        BackColor = Color.Transparent;
        ForeColor = Color.FromArgb(76, 76, 76);
        _Minimum = 0;
        _Maximum = 100;
        Font = new Font("Tahoma", 11);
        Size = new Size(70, 28);
        MinimumSize = new Size(62, 28);
        DoubleBuffered = true;

        LongPressTimer.Tick += LongPressTimer_Tick;
        LongPressTimer.Interval = 300;
    }

    public void Increment(int Value)
    {
        this._Value += Value;
        Invalidate();
    }

    public void Decrement(int Value)
    {
        this._Value -= Value;
        Invalidate();
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var B = new Bitmap(Width, Height);
        var G = Graphics.FromImage(B);

        var BackgroundLGB = new LinearGradientBrush(ClientRectangle, Color.FromArgb(246, 246, 246), Color.FromArgb(254, 254, 254), 90.0F);
        G.SmoothingMode = SmoothingMode.AntiAlias;

        G.Clear(Color.Transparent);
        G.FillPath(BackgroundLGB, Shape);
        G.DrawPath(P1, Shape);

        G.DrawString("+", new Font("Tahoma", 14), new SolidBrush(Color.FromArgb(75, 75, 75)), new Rectangle(Width - 25, 1, 19, 30));
        G.DrawLine(new Pen(Color.FromArgb(229, 228, 227)), Width - 28, 1, Width - 28, this.Height - 2);
        G.DrawString("-", new Font("Tahoma", 14), new SolidBrush(Color.FromArgb(75, 75, 75)), new Rectangle(Width - 44, 1, 19, 30));
        G.DrawLine(new Pen(Color.FromArgb(229, 228, 227)), Width - 48, 1, Width - 48, this.Height - 2);

        switch (TextAlignment)
        {
            case _TextAlignment.Near:
                G.DrawString(System.Convert.ToString(Value), Font, new SolidBrush(ForeColor), new Rectangle(5, 0, Width - 1, Height - 1), new StringFormat() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
                break;
            case _TextAlignment.Center:
                G.DrawString(System.Convert.ToString(Value), Font, new SolidBrush(ForeColor), new Rectangle(0, 0, Width - 1, Height - 1), new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                break;
        }
        e.Graphics.DrawImage((Image)B.Clone(), 0, 0);
        G.Dispose();
        B.Dispose();
    }
}

#endregion NumericUpDown
#region TrackBar

[DefaultEvent("ValueChanged")]
public class Ambiance_TrackBar : Control
{
    #region Enums

    public enum ValueDivisor
    {
        By1 = 1,
        By10 = 10,
        By100 = 100,
        By1000 = 1000
    }

    #endregion Enums
    #region Variables

    private GraphicsPath PipeBorder;
    private GraphicsPath FillValue;
    private Rectangle TrackBarHandleRect;
    private bool Cap;
    private int ValueDrawer;

    private Size ThumbSize = new(15, 15);
    private Rectangle TrackThumb;
    private int _Value = 0;

    #endregion Variables
    #region Properties

    public int Minimum
    {
        get;
        set
        {
            if (value >= Maximum)
            {
                value = Maximum - 10;
            }
            if (_Value < value)
            {
                _Value = value;
            }

            field = value;
            Invalidate();
        }
    } = 0;

    public int Maximum
    {
        get;
        set
        {
            if (value <= Minimum)
            {
                value = Minimum + 10;
            }
            if (_Value > value)
            {
                _Value = value;
            }

            field = value;
            Invalidate();
        }
    } = 10;

    public delegate void ValueChangedEventHandler();
    private ValueChangedEventHandler ValueChangedEvent;

    public event ValueChangedEventHandler ValueChanged
    {
        add => ValueChangedEvent = (ValueChangedEventHandler)System.Delegate.Combine(ValueChangedEvent, value); remove => ValueChangedEvent = (ValueChangedEventHandler)System.Delegate.Remove(ValueChangedEvent, value);
    }

    public int Value
    {
        get => _Value;
        set
        {
            if (_Value != value)
            {
                _Value = value < Minimum ? Minimum : value > Maximum ? Maximum : value;
                Invalidate();
                ValueChangedEvent?.Invoke();
            }
        }
    }

    public ValueDivisor ValueDivison
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = ValueDivisor.By1;

    [Browsable(false)]
    public float ValueToSet
    {
        get => _Value / (int)ValueDivison; set => Value = (int)(value * (int)ValueDivison);
    }

    public bool JumpToMouse
    {
        get;
        set
        {
            field = value;
            Invalidate();
        }
    } = false;

    public bool DrawValueString
    {
        get;
        set
        {
            field = value;
            Height = field == true ? 35 : 22;
            Invalidate();
        }
    } = false;

    #endregion Properties
    #region EventArgs

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        checked
        {
            var flag = this.Cap && e.X > -1 && e.X < this.Width + 1;
            if (flag)
            {
                this.Value = Minimum + (int)Math.Round((Maximum - Minimum) * (e.X / (double)this.Width));
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var flag = e.Button == MouseButtons.Left;
        checked
        {
            if (flag)
            {
                this.ValueDrawer = (int)Math.Round((this._Value - Minimum) / (double)(Maximum - Minimum) * (this.Width - 11));
                this.TrackBarHandleRect = new Rectangle(this.ValueDrawer, 0, 25, 25);
                this.Cap = this.TrackBarHandleRect.Contains(e.Location);
                _ = this.Focus();
                flag = JumpToMouse;
                if (flag)
                {
                    this.Value = Minimum + (int)Math.Round((Maximum - Minimum) * (e.X / (double)this.Width));
                }
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Cap = false;
    }

    #endregion EventArgs

    public Ambiance_TrackBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.DoubleBuffer, true);

        Size = new Size(80, 22);
        MinimumSize = new Size(47, 22);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Height = DrawValueString == true ? 35 : 22;
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var G = e.Graphics;

        G.Clear(Parent.BackColor);
        G.SmoothingMode = SmoothingMode.AntiAlias;
        TrackThumb = new Rectangle(8, 10, Width - 16, 2);
        PipeBorder = RoundRectangle.RoundRect(1, 8, Width - 3, 5, 2);

        try
        {
            this.ValueDrawer = (int)Math.Round((this._Value - Minimum) / (double)(Maximum - Minimum) * (this.Width - 11));
        }
        catch (Exception)
        {
        }

        TrackBarHandleRect = new Rectangle(ValueDrawer, 0, 10, 20);

        G.SetClip(PipeBorder);
        G.FillPath(new SolidBrush(Color.FromArgb(221, 221, 221)), PipeBorder);
        FillValue = RoundRectangle.RoundRect(1, 8, TrackBarHandleRect.X + TrackBarHandleRect.Width - 4, 5, 2);

        G.ResetClip();

        G.SmoothingMode = SmoothingMode.HighQuality;
        G.DrawPath(new Pen(Color.FromArgb(200, 200, 200)), PipeBorder);
        G.FillPath(new SolidBrush(Color.FromArgb(217, 99, 50)), FillValue);

        G.FillEllipse(new SolidBrush(Color.FromArgb(244, 244, 244)), this.TrackThumb.X + (int)Math.Round(unchecked(TrackThumb.Width * (Value / (double)this.Maximum))) - (int)Math.Round(ThumbSize.Width / 2.0), this.TrackThumb.Y + (int)Math.Round(TrackThumb.Height / 2.0) - (int)Math.Round(ThumbSize.Height / 2.0), this.ThumbSize.Width, this.ThumbSize.Height);
        G.DrawEllipse(new Pen(Color.FromArgb(180, 180, 180)), this.TrackThumb.X + (int)Math.Round(unchecked(TrackThumb.Width * (Value / (double)this.Maximum))) - (int)Math.Round(ThumbSize.Width / 2.0), this.TrackThumb.Y + (int)Math.Round(TrackThumb.Height / 2.0) - (int)Math.Round(ThumbSize.Height / 2.0), this.ThumbSize.Width, this.ThumbSize.Height);

        if (DrawValueString == true)
        {
            G.DrawString(System.Convert.ToString(ValueToSet), Font, Brushes.DimGray, 1, 20);
        }
    }
}

#endregion TrackBar
#region Panel

public class Ambiance_Panel : ContainerControl
{
    public Ambiance_Panel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Opaque, false);
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        var G = e.Graphics;

        this.Font = new Font("Tahoma", 9);
        this.BackColor = Color.White;
        G.SmoothingMode = SmoothingMode.AntiAlias;
        G.FillRectangle(new SolidBrush(Color.White), new Rectangle(0, 0, Width, Height));
        G.FillRectangle(new SolidBrush(Color.White), new Rectangle(0, 0, Width - 1, Height - 1));
        G.DrawRectangle(new Pen(Color.FromArgb(211, 208, 205)), 0, 0, Width - 1, Height - 1);
    }
}

#endregion Panel
#region ListBox

public class Ambiance_ListBox : ListBox
{
    public Ambiance_ListBox()
    {
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        this.DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        ItemHeight = 18;
        Font = new Font("Seoge UI", 11, FontStyle.Regular);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        base.OnDrawItem(e);
        e.DrawBackground();
        var LGB = new LinearGradientBrush(e.Bounds, Color.FromArgb(246, 132, 85), Color.FromArgb(231, 108, 57), 90.0F);
        if (System.Convert.ToInt32(e.State & DrawItemState.Selected) == (int)DrawItemState.Selected)
        {
            e.Graphics.FillRectangle(LGB, e.Bounds);
        }
        using (var b = new SolidBrush(e.ForeColor))
        {
            if (base.Items.Count == 0)
            {
                return;
            }
            else
            {
                e.Graphics.DrawString(base.GetItemText(base.Items[e.Index]), e.Font, b, e.Bounds);
            }
        }

        LGB.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var MyRegion = new Region(e.ClipRectangle);
        e.Graphics.FillRegion(new SolidBrush(this.BackColor), MyRegion);

        if (this.Items.Count > 0)
        {
            for (var i = 0; i <= this.Items.Count - 1; i++)
            {
                var RegionRect = this.GetItemRectangle(i);
                if (e.ClipRectangle.IntersectsWith(RegionRect))
                {
                    if ((this.SelectionMode == SelectionMode.One && this.SelectedIndex == i) || (this.SelectionMode == SelectionMode.MultiSimple && this.SelectedIndices.Contains(i)) || (this.SelectionMode == SelectionMode.MultiExtended && this.SelectedIndices.Contains(i)))
                    {
                        OnDrawItem(new DrawItemEventArgs(e.Graphics, this.Font, RegionRect, i, DrawItemState.Selected, this.ForeColor, this.BackColor));
                    }
                    else
                    {
                        OnDrawItem(new DrawItemEventArgs(e.Graphics, this.Font, RegionRect, i, DrawItemState.Default, Color.FromArgb(60, 60, 60), this.BackColor));
                    }
                    MyRegion.Complement(RegionRect);
                }
            }
        }
    }
}

#endregion ListBox
#region TabControl

public class Ambiance_TabControl : TabControl
{
    public Ambiance_TabControl() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

    protected override void CreateHandle()
    {
        base.CreateHandle();

        ItemSize = new Size(80, 24);
        Alignment = TabAlignment.Top;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var G = e.Graphics;
        _ = new Rectangle();
        G.Clear(Parent.BackColor);
        for (var TabIndex = 0; TabIndex <= TabCount - 1; TabIndex++)
        {
            _ = GetTabRect(TabIndex);
            if (!(TabIndex == SelectedIndex))
            {
                G.DrawString(TabPages[TabIndex].Text, new Font(Font.Name, Font.Size - 2, FontStyle.Bold), new SolidBrush(Color.FromArgb(80, 76, 76)), new Rectangle(GetTabRect(TabIndex).Location, GetTabRect(TabIndex).Size), new StringFormat
                {
                    LineAlignment = StringAlignment.Center,
                    Alignment = StringAlignment.Center
                });
            }
        }

        G.FillPath(new SolidBrush(Color.FromArgb(247, 246, 246)), RoundRectangle.RoundRect(0, 23, Width - 1, Height - 24, 2));
        G.DrawPath(new Pen(Color.FromArgb(201, 198, 195)), RoundRectangle.RoundRect(0, 23, Width - 1, Height - 24, 2));

        for (var ItemIndex = 0; ItemIndex <= TabCount - 1; ItemIndex++)
        {
            var ItemBoundsRect = GetTabRect(ItemIndex);
            if (ItemIndex == SelectedIndex)
            {
                G.DrawPath(new Pen(Color.FromArgb(201, 198, 195)), RoundRectangle.RoundedTopRect(new Rectangle(new Point(ItemBoundsRect.X - 2, ItemBoundsRect.Y - 2), new Size(ItemBoundsRect.Width + 3, ItemBoundsRect.Height)), 7));
                G.FillPath(new SolidBrush(Color.FromArgb(247, 246, 246)), RoundRectangle.RoundedTopRect(new Rectangle(new Point(ItemBoundsRect.X - 1, ItemBoundsRect.Y - 1), new Size(ItemBoundsRect.Width + 2, ItemBoundsRect.Height)), 7));

                try
                {
                    G.DrawString(TabPages[ItemIndex].Text, new Font(Font.Name, Font.Size - 1, FontStyle.Bold), new SolidBrush(Color.FromArgb(80, 76, 76)), new Rectangle(GetTabRect(ItemIndex).Location, GetTabRect(ItemIndex).Size), new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        Alignment = StringAlignment.Center
                    });
                    TabPages[ItemIndex].BackColor = Color.FromArgb(247, 246, 246);
                }
                catch
                {
                }
            }
        }
    }
}

#endregion TabControl
