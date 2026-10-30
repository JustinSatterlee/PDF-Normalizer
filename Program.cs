using System.Drawing.Drawing2D;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNormalizer;

static class Program
{
    const double LetterWidth = 612;  // 8.5 in
    const double LetterHeight = 792; // 11 in

    internal const string DefaultPattern = @"[path_to_file]/[file_name].letter.pdf";

    static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PdfNormalizer",
        "save-path.txt");

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            SelfCheck();
            Console.WriteLine("self-check ok");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Scale source into dest, uniform, centered. Returns dest rect in dest coords.</summary>
    internal static XRect Fit(double srcW, double srcH, double destW, double destH)
    {
        if (srcW <= 0 || srcH <= 0) throw new ArgumentOutOfRangeException(nameof(srcW));
        double scale = Math.Min(destW / srcW, destH / srcH);
        double w = srcW * scale;
        double h = srcH * scale;
        return new XRect((destW - w) / 2, (destH - h) / 2, w, h);
    }

    // [file_name] is the name without its extension, so [path_to_file]/[file_name]-fixed.pdf
    // lands next to the original. A pattern that does not end in .pdf keeps the original
    // extension, which makes [path_to_file]/[file_name] overwrite that file.
    internal static string ResolveOutputPath(string inputPath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new InvalidOperationException("Enter a save path.");

        string fullInput = Path.GetFullPath(inputPath);
        string dir = Path.GetDirectoryName(fullInput) ?? "";
        string name = Path.GetFileNameWithoutExtension(fullInput);
        string result = pattern.Trim()
            .Replace("[path_to_file]", dir, StringComparison.OrdinalIgnoreCase)
            .Replace("[file_name]", name, StringComparison.OrdinalIgnoreCase);

        if (!result.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(fullInput);
            result += ext.Length == 0 ? ".pdf" : ext;
        }

        if (!Path.IsPathRooted(result))
            result = Path.Combine(dir, result);
        return Path.GetFullPath(result);
    }

    internal static string LoadPattern()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string text = File.ReadAllText(SettingsFile).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch { /* use the default */ }
        return DefaultPattern;
    }

    internal static void SavePattern(string pattern)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, pattern);
        }
        catch { /* the box still works for this run */ }
    }

    // ponytail: CropBox, annotations, and AcroForms are dropped; upgrade by copying those dicts if a phone PDF needs them.
    internal static string Normalize(string inputPath, string? pattern = null)
    {
        string finalPath = ResolveOutputPath(inputPath, string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            "." + Path.GetFileNameWithoutExtension(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp.pdf");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            // Read into memory so a pattern that names the original file can replace it.
            byte[] bytes = File.ReadAllBytes(inputPath);
            using (var source = new MemoryStream(bytes))
            using (var form = XPdfForm.FromStream(source))
            using (var output = new PdfDocument())
            {
                for (int i = 0; i < form.PageCount; i++)
                {
                    form.PageNumber = i + 1;
                    int rotate = NormalizeRotate(form.Page?.Rotate ?? 0);

                    double mediaW = form.PointWidth;
                    double mediaH = form.PointHeight;
                    double visualW = rotate is 90 or 270 ? mediaH : mediaW;
                    double visualH = rotate is 90 or 270 ? mediaW : mediaH;

                    var page = output.AddPage();
                    page.Width = XUnit.FromPoint(LetterWidth);
                    page.Height = XUnit.FromPoint(LetterHeight);
                    page.Rotate = 0;

                    XRect dest = Fit(visualW, visualH, LetterWidth, LetterHeight);
                    using var gfx = XGraphics.FromPdfPage(page);
                    DrawPage(gfx, form, dest, rotate);
                }

                output.Save(tempPath);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    static int NormalizeRotate(int rotate) => ((rotate % 360) + 360) % 360;

    static void DrawPage(XGraphics gfx, XPdfForm form, XRect dest, int rotate)
    {
        gfx.Save();
        switch (rotate)
        {
            case 0:
                gfx.DrawImage(form, dest);
                break;
            case 90:
                gfx.TranslateTransform(dest.Right, dest.Top);
                gfx.RotateTransform(90);
                gfx.DrawImage(form, 0, 0, dest.Height, dest.Width);
                break;
            case 180:
                gfx.TranslateTransform(dest.Right, dest.Bottom);
                gfx.RotateTransform(180);
                gfx.DrawImage(form, 0, 0, dest.Width, dest.Height);
                break;
            case 270:
                gfx.TranslateTransform(dest.Left, dest.Bottom);
                gfx.RotateTransform(270);
                gfx.DrawImage(form, 0, 0, dest.Height, dest.Width);
                break;
            default:
                gfx.DrawImage(form, dest);
                break;
        }
        gfx.Restore();
    }

    static void SelfCheck()
    {
        XRect wide = Fit(1000, 500, LetterWidth, LetterHeight);
        Assert(Near(wide.Width, LetterWidth), "wide should fill width");
        Assert(Near(wide.Height, LetterWidth * 0.5), "wide height scales");
        Assert(Near(wide.X, 0), "wide x");
        Assert(wide.Y > 0, "wide has top/bottom bars");

        XRect tall = Fit(400, 800, LetterWidth, LetterHeight);
        Assert(Near(tall.Height, LetterHeight), "tall should fill height");
        Assert(tall.X > 0, "tall has side bars");

        XRect exact = Fit(LetterWidth, LetterHeight, LetterWidth, LetterHeight);
        Assert(Near(exact.X, 0) && Near(exact.Y, 0), "exact fills letter");
        Assert(Near(exact.Width, LetterWidth) && Near(exact.Height, LetterHeight), "exact size");

        string fixedPath = ResolveOutputPath(@"C:\docs\scan.pdf", @"[path_to_file]/[file_name]-fixed.pdf");
        Assert(SamePath(fixedPath, @"C:\docs\scan-fixed.pdf"), "fixed pattern: " + fixedPath);
        string same = ResolveOutputPath(@"C:\docs\scan.PDF", @"[path_to_file]/[file_name]");
        Assert(SamePath(same, @"C:\docs\scan.PDF"), "overwrite pattern: " + same);
        string letter = ResolveOutputPath(@"C:\docs\scan.pdf", DefaultPattern);
        Assert(SamePath(letter, @"C:\docs\scan.letter.pdf"), "default pattern: " + letter);

        string dir = Path.Combine(Path.GetTempPath(), "PdfNormalizerSelfCheck");
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, $"in-{Guid.NewGuid():N}.pdf");
        string? output = null;
        try
        {
            using (var doc = new PdfDocument())
            {
                var p1 = doc.AddPage();
                p1.Width = XUnit.FromPoint(400);
                p1.Height = XUnit.FromPoint(600);
                using (var g = XGraphics.FromPdfPage(p1))
                    g.DrawRectangle(XBrushes.LightGray, 0, 0, 400, 600);

                var p2 = doc.AddPage();
                p2.Width = XUnit.FromPoint(900);
                p2.Height = XUnit.FromPoint(500);
                using (var g = XGraphics.FromPdfPage(p2))
                    g.DrawRectangle(XBrushes.LightBlue, 0, 0, 900, 500);

                var p3 = doc.AddPage();
                p3.Width = XUnit.FromPoint(500);
                p3.Height = XUnit.FromPoint(700);
                p3.Rotate = 90;
                using (var g = XGraphics.FromPdfPage(p3))
                    g.DrawRectangle(XBrushes.LightGreen, 0, 0, 500, 700);

                doc.Save(input);
            }

            output = Normalize(input, @"[path_to_file]/[file_name]-fixed.pdf");
            Assert(SamePath(output, Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + "-fixed.pdf")), "fixed output path");
            AssertPages(output);

            string overwritten = Normalize(input, @"[path_to_file]/[file_name]");
            Assert(SamePath(overwritten, input), "overwrite path");
            AssertPages(overwritten);
        }
        finally
        {
            TryDelete(input);
            if (output != null) TryDelete(output);
        }
    }

    static void AssertPages(string path)
    {
        using var result = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert(result.PageCount == 3, "page count");
        foreach (PdfPage page in result.Pages)
        {
            Assert(Near(page.Width.Point, LetterWidth), $"width {page.Width.Point}");
            Assert(Near(page.Height.Point, LetterHeight), $"height {page.Height.Point}");
            Assert(page.Rotate == 0, $"rotate {page.Rotate}");
        }
    }

    static void Assert(bool ok, string msg)
    {
        if (!ok) throw new InvalidOperationException("self-check failed: " + msg);
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

sealed class MainForm : Form
{
    readonly TextBox _pattern;
    readonly DropTarget _drop;
    readonly Label _status;
    bool _busy;

    public MainForm()
    {
        Text = "PDF Letter Normalizer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 256);
        AllowDrop = true;

        _drop = new DropTarget
        {
            Location = new Point(12, 12),
            Size = new Size(536, 96),
        };
        _drop.Click += (_, _) => ChooseFiles();
        _drop.DragEnter += OnDragEnter;
        _drop.DragDrop += OnDragDrop;

        var saveLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(12, 116),
            Size = new Size(536, 20),
            Text = "Save as",
        };

        _pattern = new TextBox
        {
            Location = new Point(12, 138),
            Size = new Size(536, 23),
            Text = Program.LoadPattern(),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _pattern.TextChanged += (_, _) => Program.SavePattern(_pattern.Text);

        var tokens = new Label
        {
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Location = new Point(12, 166),
            Size = new Size(536, 36),
            Text = "Use variables [path_to_file] and [file_name] for a dynamic file path..",
        };

        _status = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(12, 206),
            Size = new Size(536, 44),
            Text = "",
        };

        Controls.Add(_drop);
        Controls.Add(saveLabel);
        Controls.Add(_pattern);
        Controls.Add(tokens);
        Controls.Add(_status);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pdfs.Length == 0)
        {
            _status.Text = "Drop a .pdf file.";
            return;
        }
        _ = ProcessAsync(pdfs);
    }

    void ChooseFiles()
    {
        if (_busy) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true,
            Title = "Choose PDF to normalize",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = ProcessAsync(dlg.FileNames);
    }

    async Task ProcessAsync(string[] paths)
    {
        _busy = true;
        _drop.Enabled = false;
        AllowDrop = false;
        string pattern = _pattern.Text;
        try
        {
            foreach (string path in paths)
            {
                _status.Text = "Working… " + Path.GetFileName(path);
                try
                {
                    string saved = await Task.Run(() => Program.Normalize(path, pattern)).ConfigureAwait(true);
                    _status.Text = "Saved: " + saved;
                }
                catch (Exception ex)
                {
                    _status.Text = "Error: " + ex.Message;
                    break;
                }
            }
        }
        finally
        {
            _busy = false;
            _drop.Enabled = true;
            AllowDrop = true;
        }
    }
}

sealed class DropTarget : Panel
{
    bool _hover;

    public DropTarget()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        AllowDrop = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Drop a PDF here, or click to choose";
        BackColor = SystemColors.Window;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool hot = Enabled && (_hover || Focused);
        Color ink = Enabled ? (hot ? SystemColors.Highlight : Color.FromArgb(120, 120, 120)) : SystemColors.GrayText;
        var bounds = new Rectangle(3, 3, Width - 7, Height - 7);

        if (hot)
        {
            using var fill = new SolidBrush(Color.FromArgb(232, 242, 252));
            using var area = Round(bounds, 10);
            g.FillPath(fill, area);
        }

        using (var pen = new Pen(ink, 1.5f) { DashStyle = DashStyle.Dash })
        using (var area = Round(bounds, 10))
            g.DrawPath(pen, area);

        const string caption = "Drop a PDF here, or click to choose";
        float icon = 28;
        const int gap = 6;
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak;
        var textSize = TextRenderer.MeasureText(g, caption, Font, new Size(Width - 24, int.MaxValue), flags);
        int block = (int)icon + gap + textSize.Height;
        int top = Math.Max(8, (Height - block) / 2);
        DrawFileDown(g, new RectangleF((Width - icon) / 2f, top, icon, icon), ink);
        var text = new Rectangle(12, top + (int)icon + gap, Width - 24, textSize.Height);
        TextRenderer.DrawText(g, caption, Font, text, ink, flags | TextFormatFlags.VerticalCenter);
    }

    // Lucide "file-down": a page with a downward arrow, the usual drop-a-file mark.
    static void DrawFileDown(Graphics g, RectangleF box, Color color)
    {
        float s = box.Width / 24f;
        PointF P(float x, float y) => new(box.X + x * s, box.Y + y * s);
        using var pen = new Pen(color, 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLines(pen, new[] { P(14, 2), P(6, 2), P(4, 4), P(4, 20), P(6, 22), P(18, 22), P(20, 20), P(20, 8), P(14, 2) });
        g.DrawLines(pen, new[] { P(14, 2), P(14, 8), P(20, 8) });
        g.DrawLine(pen, P(12, 11), P(12, 18));
        g.DrawLines(pen, new[] { P(8.5f, 15), P(12, 18.5f), P(15.5f, 15) });
    }

    static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
