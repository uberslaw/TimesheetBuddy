using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfColor = QuestPDF.Infrastructure.Color;
using PdfColors = QuestPDF.Helpers.Colors;

namespace TimesheetBuddy.Services;

public static class TimesheetExport
{
    private static readonly string[] Columns = ["Date", "Task", "Job code", "Category", "Duration", "Notes"];

    public static string DefaultFileName(IReadOnlyList<WorkWeek> weeks, string extension)
    {
        if (weeks.Count == 0)
            throw new ArgumentException("Choose at least one week to export.", nameof(weeks));

        var ordered = weeks.OrderBy(week => week.Monday).ToList();
        var first = ordered[0].Monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = ordered[^1].Friday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var suffix = extension.StartsWith('.') ? extension : "." + extension;
        return ordered.Count == 1
            ? $"Timesheet-{first}-to-{last}{suffix}"
            : $"Timesheet-{ordered.Count}-weeks-{first}-to-{last}{suffix}";
    }

    public static void WriteExcel(string path, IReadOnlyList<WorkWeek> weeks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (weeks.Count == 0)
            throw new ArgumentException("Choose at least one week to export.", nameof(weeks));

        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "Timesheet Buddy";
        workbook.Properties.Author = "Timesheet Buddy";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var week in weeks.OrderBy(item => item.Monday))
        {
            var sheet = workbook.Worksheets.Add(SheetName(week, used));
            WriteSheet(sheet, week);
        }

        workbook.SaveAs(path);
    }

    public static void WritePdf(string path, IReadOnlyList<WorkWeek> weeks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (weeks.Count == 0)
            throw new ArgumentException("Choose at least one week to export.", nameof(weeks));

        QuestPDF.Settings.License = LicenseType.Community;
        var ordered = weeks.OrderBy(week => week.Monday).ToList();

        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.PageColor(PdfColors.White);
                page.DefaultTextStyle(style => style.FontSize(10).FontColor(PdfColors.Black));

                page.Header().Column(header =>
                {
                    header.Item().Text("Timesheet Buddy").FontSize(12).SemiBold().FontColor(Accent);
                    header.Item().PaddingTop(4).LineHorizontal(1).LineColor(Line);
                });

                page.Footer().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(9).FontColor(PdfColors.Grey.Darken1));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });

                page.Content().PaddingVertical(10).Column(column =>
                {
                    column.Spacing(6);
                    for (var index = 0; index < ordered.Count; index++)
                    {
                        if (index > 0)
                            column.Item().PageBreak();

                        ComposeWeek(column, ordered[index]);
                    }
                });
            });
        }).GeneratePdf(path);
    }

    private static void ComposeWeek(ColumnDescriptor column, WorkWeek week)
    {
        column.Item().Text(week.Label).FontSize(16).SemiBold().FontColor(PdfColors.Black);
        column.Item().Text(SummaryLine(week)).FontColor(PdfColors.Grey.Darken2);

        var days = week.Days.Where(day => day.Tasks.Count > 0).ToList();
        if (days.Count == 0)
        {
            column.Item().PaddingTop(6).Text("No time logged this week.").Italic().FontColor(PdfColors.Grey.Darken1);
            return;
        }

        foreach (var day in days)
        {
            column.Item().PaddingTop(8).Text(day.Heading).SemiBold().FontSize(12);
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(2.2f);
                    columns.RelativeColumn(1.8f);
                    columns.RelativeColumn(1.2f);
                    columns.ConstantColumn(62);
                    columns.RelativeColumn(2.2f);
                });

                table.Header(header =>
                {
                    foreach (var title in Columns)
                    {
                        header.Cell().Background(AccentSoft).Padding(4)
                            .Text(title).SemiBold().FontSize(9);
                    }
                });

                foreach (var task in day.Tasks)
                {
                    BodyCell(table, task.DateDisplay);
                    BodyCell(table, task.TaskName);
                    BodyCell(table, DisplayOrDash(task.JobCode));
                    BodyCell(table, DisplayOrDash(task.Category));
                    BodyCell(table, task.DurationDisplay);
                    BodyCell(table, task.Notes);
                }
            });
        }
    }

    private static void BodyCell(TableDescriptor table, string text)
    {
        table.Cell().BorderBottom(0.4f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(4)
            .Text(text).FontSize(9);
    }

    private static void WriteSheet(IXLWorksheet sheet, WorkWeek week)
    {
        sheet.Cell(1, 1).Value = "Week";
        sheet.Cell(1, 2).Value = week.Label;
        sheet.Range(1, 2, 1, 6).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 2).Style.Font.Bold = true;
        sheet.Cell(1, 2).Style.Font.FontSize = 14;

        sheet.Cell(2, 1).Value = "Tasks";
        sheet.Cell(2, 2).Value = week.TaskCount;
        sheet.Cell(3, 1).Value = "Hours";
        sheet.Cell(3, 2).Value = week.HoursDisplay;
        sheet.Cell(2, 1).Style.Font.Bold = true;
        sheet.Cell(3, 1).Style.Font.Bold = true;

        if (week.TaskCount == 0)
            sheet.Cell(4, 1).Value = "No time logged this week.";

        const int headerRow = 5;
        for (var column = 0; column < Columns.Length; column++)
        {
            var cell = sheet.Cell(headerRow, column + 1);
            cell.Value = Columns[column];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D8F3EA");
        }

        var row = headerRow + 1;
        foreach (var day in week.Days)
        {
            foreach (var task in day.Tasks)
            {
                sheet.Cell(row, 1).Value = task.DateDisplay;
                sheet.Cell(row, 2).Value = task.TaskName;
                sheet.Cell(row, 3).Value = task.JobCode;
                sheet.Cell(row, 4).Value = task.Category;
                sheet.Cell(row, 5).Value = task.DurationDisplay;
                sheet.Cell(row, 6).Value = task.Notes;
                row++;
            }
        }

        sheet.Column(1).Width = 22;
        sheet.Column(2).Width = 32;
        sheet.Column(3).Width = 28;
        sheet.Column(4).Width = 16;
        sheet.Column(5).Width = 14;
        sheet.Column(6).Width = 40;
        sheet.Column(2).Style.Alignment.WrapText = true;
        sheet.Column(6).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(headerRow);
        if (row > headerRow + 1)
            sheet.Range(headerRow, 1, row - 1, Columns.Length).SetAutoFilter();
    }

    private static string SheetName(WorkWeek week, HashSet<string> used)
    {
        var stem = week.Monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = stem;
        var duplicate = 2;
        while (!used.Add(name))
        {
            var suffix = $" ({duplicate++})";
            var trimmed = stem.Length + suffix.Length <= 31
                ? stem
                : stem[..(31 - suffix.Length)];
            name = trimmed + suffix;
        }

        return name;
    }

    private static string SummaryLine(WorkWeek week) =>
        week.TaskCount == 1
            ? $"1 task · {week.HoursDisplay} logged"
            : $"{week.TaskCount} tasks · {week.HoursDisplay} logged";

    private static string DisplayOrDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static PdfColor Accent => PdfColor.FromHex("#0F6E56");
    private static PdfColor AccentSoft => PdfColor.FromHex("#D8F3EA");
    private static PdfColor Line => PdfColor.FromHex("#D5DBE3");
}
