using System.Globalization;
using EventPermits.Application;
using QRCoder;

namespace EventPermits.Infrastructure;

/// <summary>
/// Server-side PDF generation (FR-09, E8-01 AC4) with the in-repo <see cref="SimplePdf"/> writer (A-31).
/// The permit QR code carries the signed token and is drawn as vector modules from QRCoder (MIT).
/// </summary>
public sealed class PdfRenderer : IPdfRenderer
{
    private static readonly (double, double, double) Brand = (0.12, 0.31, 0.35);
    private static readonly (double, double, double) Muted = (0.29, 0.33, 0.40);

    public byte[] RenderPermit(PermitPdfModel m)
    {
        var pdf = new SimplePdf($"Permit {m.PermitNumber} v{m.Version}", "Memorial Event Operations (fictional practice)");
        Banner(pdf, "Memorial Event Permit");
        pdf.Paragraph($"{m.PermitNumber}  -  version {m.Version}", 20, SimplePdf.Bold);
        pdf.Paragraph($"Application {m.ApplicationReference}", 10, color: Muted);
        pdf.Y += 8;

        var rows = new List<(string, string)>
        {
            ("Event", m.EventTitle), ("Category", m.Category), ("Applicant", m.ApplicantName), ("Venue", $"{m.VenueName}, {m.CemeteryName}"),
            ("Schedule", $"{m.LocalWindow} ({m.TimeZone})"), ("Attendees", m.Attendees.ToString(CultureInfo.InvariantCulture)),
            ("Issued by", $"{m.IssuedBy} on {m.IssuedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC"),
        };
        if (!string.IsNullOrWhiteSpace(m.ReissueReason)) rows.Add(("Reissued because", m.ReissueReason));
        foreach (var (label, value) in rows)
        {
            var top = pdf.Y;
            pdf.Text(pdf.Margin, top, label, 10, color: Muted);
            pdf.Y = top;
            pdf.Paragraph(value, 10.5, x: pdf.Margin + 120);
            pdf.Y += 2;
        }

        Section(pdf, "Conditions and restrictions");
        foreach (var line in m.Conditions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            pdf.Paragraph("•  " + line, 10, x: pdf.Margin + 8);

        Section(pdf, "Day-of-event verification");
        pdf.Paragraph("Show this QR code to the event attendant, or read out the short code.", 10);
        pdf.EnsureSpace(150);
        var qrTop = pdf.Y + 4;
        pdf.Matrix(pdf.Margin, qrTop, 130, QrModules(m.Token));
        pdf.Text(pdf.Margin + 150, qrTop + 10, "Short code", 10, color: Muted);
        pdf.Text(pdf.Margin + 150, qrTop + 26, m.ShortCode, 20, SimplePdf.Bold);
        pdf.Y = qrTop + 60;
        pdf.Paragraph("Token: " + m.Token, 6.5, x: pdf.Margin + 150, color: Muted);
        pdf.Y = Math.Max(pdf.Y, qrTop + 140);
        pdf.Paragraph("Only the newest version of this permit is valid. Revoked, superseded or cancelled permits are rejected at check-in.", 9, color: Muted);
        Footer(pdf, "Fictional practice document - not issued by the VA or any real agency.");
        return pdf.Save();
    }

    public byte[] RenderRunOfShow(string title, IReadOnlyList<string[]> rows, string footer)
    {
        var pdf = new SimplePdf(title, "Memorial Event Operations (fictional practice)", landscape: true) { Margin = 36 };
        Banner(pdf, title);
        double[] widths = [70, 110, 180, 120, 90, 60, 140];
        var scale = (pdf.Width - 2 * pdf.Margin) / widths.Sum();
        widths = widths.Select(w => w * scale).ToArray();
        foreach (var (cells, index) in rows.Select((r, i) => (r, i)))
        {
            var header = index == 0;
            var font = header ? SimplePdf.Bold : SimplePdf.Regular;
            var wrapped = cells.Take(widths.Length).Select((c, i) => SimplePdf.Wrap(c, 9, font, widths[i] - 8)).ToList();
            var height = wrapped.Max(w => w.Count) * 11.5 + 8;
            pdf.EnsureSpace(height);
            if (header) pdf.Rect(pdf.Margin, pdf.Y, widths.Sum(), height, (0.95, 0.95, 0.93));
            var x = pdf.Margin;
            for (var c = 0; c < wrapped.Count; c++)
            {
                for (var l = 0; l < wrapped[c].Count; l++) pdf.Text(x + 4, pdf.Y + 4 + l * 11.5, wrapped[c][l], 9, font);
                x += widths[c];
            }
            pdf.Y += height;
            pdf.Line(pdf.Margin, pdf.Y, pdf.Margin + widths.Sum(), pdf.Y);
        }
        pdf.Y += 10;
        pdf.Paragraph(footer, 9, color: Muted);
        Footer(pdf, "Fictional practice document.");
        return pdf.Save();
    }

    public static IReadOnlyList<IReadOnlyList<bool>> QrModules(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return data.ModuleMatrix.Select(row => (IReadOnlyList<bool>)row.Cast<bool>().ToList()).ToList();
    }

    private static void Banner(SimplePdf pdf, string title)
    {
        pdf.Rect(0, 0, pdf.Width, 6, Brand);
        pdf.Paragraph(title.ToUpperInvariant(), 10, SimplePdf.Bold, color: Brand);
        pdf.Paragraph("Memorial Event Operations - fictional practice system", 8, color: Muted);
        pdf.Line(pdf.Margin, pdf.Y + 2, pdf.Width - pdf.Margin, pdf.Y + 2);
        pdf.Y += 14;
    }

    private static void Section(SimplePdf pdf, string title)
    {
        pdf.Y += 12;
        pdf.EnsureSpace(40);
        pdf.Paragraph(title, 12, SimplePdf.Bold);
        pdf.Y += 2;
    }

    private static void Footer(SimplePdf pdf, string text) => pdf.Text(pdf.Margin, pdf.Height - pdf.Margin + 12, text, 7.5, color: Muted);
}
