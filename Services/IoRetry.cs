using System.IO;
using System.Text;

namespace TimesheetBuddy.Services;

/// <summary>
/// Short retries for file operations that fail while another program briefly holds the file
/// (OneDrive uploading it, an antivirus scan). No WPF here.
/// </summary>
public static class IoRetry
{
    /// <summary>About 3 s in total before giving up.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600)
    ];

    public static Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

    public static void Run(Action action, IReadOnlyList<TimeSpan>? delays = null) =>
        Run<object?>(() =>
        {
            action();
            return null;
        }, delays);

    public static T Run<T>(Func<T> action, IReadOnlyList<TimeSpan>? delays = null)
    {
        delays ??= DefaultDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < delays.Count)
            {
                Sleep(delays[attempt]);
            }
        }
    }

    /// <summary>Sharing violations and access denied are retried; a missing folder or drive is not.</summary>
    public static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException && ex is not DirectoryNotFoundException && ex is not DriveNotFoundException &&
         ex is not FileNotFoundException && ex is not PathTooLongException);

    public static string ReadAllText(string path, IReadOnlyList<TimeSpan>? delays = null) =>
        Run(() => File.ReadAllText(path), delays);

    /// <summary>
    /// Writes to <c>path.tmp</c> (flushed to disk), then moves it over <paramref name="path"/>, so the file is
    /// either the old or the new version and never a truncated one. Retried as a whole.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string text, IReadOnlyList<TimeSpan>? delays = null)
    {
        var temp = path + ".tmp";
        try
        {
            Run(() =>
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                           FileOptions.WriteThrough))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }, delays);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}
