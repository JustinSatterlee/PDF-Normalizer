using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNormalizer;

static class Program
{
    const double LetterWidth = 612;  // 8.5 in
    const double LetterHeight = 792; // 11 in

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

    internal static string NextOutputPath(string inputPath)
    {
        string dir = Path.GetDirectoryName(inputPath)!;
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string candidate = Path.Combine(dir, $"{name}.letter.pdf");
        if (!File.Exists(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{name}.letter ({n}).pdf");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ponytail: CropBox, annotations, and AcroForms are dropped; upgrade by copying those dicts if a phone PDF needs them.
    internal static string Normalize(string inputPath)
    {
        string finalPath = NextOutputPath(inputPath);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp.pdf");

        try
        {
            using var form = XPdfForm.FromFile(inputPath);
            using var output = new PdfDocument();

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
            File.Move(tempPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
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
        // Fit math
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

            output = Normalize(input);
            using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert(result.PageCount == 3, "page count");
            foreach (PdfPage page in result.Pages)
            {
                Assert(Near(page.Width.Point, LetterWidth), $"width {page.Width.Point}");
                Assert(Near(page.Height.Point, LetterHeight), $"height {page.Height.Point}");
                Assert(page.Rotate == 0, $"rotate {page.Rotate}");
            }
        }
        finally
        {
            TryDelete(input);
            if (output != null) TryDelete(output);
        }
    }

    static void Assert(bool ok, string msg)
    {
        if (!ok) throw new InvalidOperationException("self-check failed: " + msg);
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

sealed class MainForm : Form
{
    readonly Label _hint;
    readonly Button _choose;
    readonly Label _status;
    bool _busy;

    public MainForm()
    {
        Text = "PDF Letter Normalizer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 160);
        AllowDrop = true;

        _hint = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 56,
            Text = "Drop a PDF here",
            Padding = new Padding(12),
        };

        _choose = new Button
        {
            Text = "Choose PDF…",
            Width = 140,
            Height = 32,
            Anchor = AnchorStyles.None,
        };
        _choose.Click += (_, _) => ChooseFiles();

        var buttonHost = new Panel { Dock = DockStyle.Top, Height = 48 };
        buttonHost.Controls.Add(_choose);
        buttonHost.Resize += (_, _) =>
        {
            _choose.Left = (buttonHost.ClientSize.Width - _choose.Width) / 2;
            _choose.Top = (buttonHost.ClientSize.Height - _choose.Height) / 2;
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "",
            Padding = new Padding(12),
        };

        Controls.Add(_status);
        Controls.Add(buttonHost);
        Controls.Add(_hint);

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
        _choose.Enabled = false;
        AllowDrop = false;
        try
        {
            foreach (string path in paths)
            {
                _status.Text = "Working… " + Path.GetFileName(path);
                try
                {
                    string saved = await Task.Run(() => Program.Normalize(path)).ConfigureAwait(true);
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
            _choose.Enabled = true;
            AllowDrop = true;
        }
    }
}
