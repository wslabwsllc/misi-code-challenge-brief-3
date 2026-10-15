using System.Globalization;
using System.Text;

namespace EventPermits.Infrastructure;

/// <summary>
/// Minimal PDF 1.4 writer (A-31): the two standard Helvetica faces (no embedded fonts), wrapped text, lines,
/// filled rectangles and colours. Enough for permits and run-of-show sheets without a third-party PDF library
/// (PDFsharp 6.5 moved to AGPL-3.0 with a licence check). Content streams are uncompressed and searchable.
/// </summary>
public sealed class SimplePdf
{
    public const string Regular = "F1", Bold = "F2";
    private readonly List<(double Width, double Height, StringBuilder Content)> _pages = [];
    private StringBuilder _page = new();
    private readonly string _title;
    private readonly string _author;

    public SimplePdf(string title, string author, bool landscape = false)
    {
        _title = title;
        _author = author;
        Width = landscape ? 842 : 595;
        Height = landscape ? 595 : 842;
        NewPage();
    }

    public double Width { get; }
    public double Height { get; }
    public double Margin { get; init; } = 50;
    /// <summary>Current vertical position measured from the top of the page.</summary>
    public double Y { get; set; }

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add((Width, Height, _page));
        Y = Margin;
    }

    public void EnsureSpace(double needed)
    {
        if (Y + needed > Height - Margin) NewPage();
    }

    public void Text(double x, double yTop, string text, double size, string font = Regular, (double R, double G, double B)? color = null)
    {
        var (r, g, b) = color ?? (0.11, 0.14, 0.19);
        _page.Append(Fmt($"{r} {g} {b} rg BT /{font} {size} Tf {x} {Height - yTop - size} Td ({Escape(text)}) Tj ET\n"));
    }

    /// <summary>Writes wrapped text at the cursor and advances it; returns the height used.</summary>
    public double Paragraph(string text, double size, string font = Regular, double? x = null, double? maxWidth = null, (double, double, double)? color = null, double spacing = 1.3)
    {
        var left = x ?? Margin;
        var width = maxWidth ?? Width - left - Margin;
        var lines = Wrap(text, size, font, width);
        foreach (var line in lines)
        {
            EnsureSpace(size * spacing);
            Text(left, Y, line, size, font, color);
            Y += size * spacing;
        }
        return lines.Count * size * spacing;
    }

    public void Line(double x1, double y1Top, double x2, double y2Top, double width = 0.75, (double R, double G, double B)? color = null)
    {
        var (r, g, b) = color ?? (0.8, 0.8, 0.8);
        _page.Append(Fmt($"{r} {g} {b} RG {width} w {x1} {Height - y1Top} m {x2} {Height - y2Top} l S\n"));
    }

    public void Rect(double x, double yTop, double w, double h, (double R, double G, double B) fill)
    {
        _page.Append(Fmt($"{fill.R} {fill.G} {fill.B} rg {x} {Height - yTop - h} {w} {h} re f\n"));
    }

    /// <summary>A QR code (or any boolean matrix) drawn as filled squares — no raster image needed.</summary>
    public void Matrix(double x, double yTop, double size, IReadOnlyList<IReadOnlyList<bool>> modules)
    {
        var n = modules.Count;
        var cell = size / n;
        Rect(x, yTop, size, size, (1, 1, 1));
        for (var row = 0; row < n; row++)
            for (var col = 0; col < n; col++)
                if (modules[row][col]) Rect(x + col * cell, yTop + row * cell, cell + 0.05, cell + 0.05, (0, 0, 0));
    }

    public byte[] Save()
    {
        var objects = new List<string>();
        var pageIds = new List<int>();
        // 1 catalog, 2 pages, 3 font regular, 4 font bold, 5 info; then (page, content) pairs.
        var next = 6;
        var pageObjects = new List<(int PageId, int ContentId, string Content, double W, double H)>();
        foreach (var (w, h, content) in _pages)
        {
            pageObjects.Add((next, next + 1, content.ToString(), w, h));
            pageIds.Add(next);
            next += 2;
        }
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        objects.Add($"<< /Title ({Escape(_title)}) /Author ({Escape(_author)}) /Producer (EventPermits SimplePdf) >>");
        foreach (var p in pageObjects)
        {
            objects.Add(Fmt($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {p.W} {p.H}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {p.ContentId} 0 R >>"));
            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(p.Content)} >>\nstream\n{p.Content}endstream");
        }

        using var output = new MemoryStream();
        void Write(string s) { var bytes = Encoding.Latin1.GetBytes(s); output.Write(bytes); }
        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    public static List<string> Wrap(string text, double size, string font, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, size, font) <= maxWidth || current.Length == 0)
                {
                    // A single word longer than the line is broken by characters.
                    if (current.Length == 0 && Measure(word, size, font) > maxWidth)
                    {
                        var chunk = new StringBuilder();
                        foreach (var ch in word)
                        {
                            if (Measure(chunk.ToString() + ch, size, font) > maxWidth) { lines.Add(chunk.ToString()); chunk.Clear(); }
                            chunk.Append(ch);
                        }
                        current = chunk;
                    }
                    else current = new StringBuilder(candidate);
                }
                else
                {
                    lines.Add(current.ToString());
                    current = new StringBuilder(word);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    public static double Measure(string text, double size, string font)
    {
        var widths = font == Bold ? BoldWidths : RegularWidths;
        double units = 0;
        foreach (var ch in text) units += ch is >= ' ' and <= '~' ? widths[ch - ' '] : 556;
        return units * size / 1000.0;
    }

    /// <summary>Escapes PDF string syntax and maps common typography to WinAnsi; anything else becomes '?'.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch switch
            {
                '–' => '\u0096', '—' => '\u0097', '•' => '\u0095', '‘' => '\u0091', '’' => '\u0092', '“' => '\u0093', '”' => '\u0094', '…' => '\u0085',
                '→' => '>', '·' => '·', '✓' => 'v', _ => ch,
            };
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 32 && c != '\t') sb.Append(' ');
            else if (c > 255) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // Adobe Helvetica / Helvetica-Bold AFM widths for ASCII 32..126 (units per 1000 em).
    private static readonly int[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];
}
