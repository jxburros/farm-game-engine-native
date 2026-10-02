using System.Diagnostics;
using System.Globalization;

namespace FarmingRpgMaker.App.Services;

/// <summary>
/// The editor's log on disk: a <see cref="TraceListener"/> that appends everything traced
/// (<c>Trace.TraceError</c>/<c>TraceWarning</c> from the editor, Avalonia's warnings through
/// <c>LogToTrace</c>) to <c>&lt;data&gt;/logs/editor-yyyy-MM-dd.log</c>. It keeps the newest
/// <see cref="KeptFiles"/> days and stops writing a day's file at <see cref="MaxFileBytes"/>.
/// A failing write is dropped: logging never takes the editor down.
/// </summary>
public sealed class FileLog : TraceListener
{
    /// <summary>Days of logs kept.</summary>
    public const int KeptFiles = 7;

    /// <summary>Cap per day's file.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private string? _pending;

    public FileLog(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _time = time ?? TimeProvider.System;
        Name = "FarmingRpgMakerFileLog";
    }

    /// <summary>The log folder (Help → About opens it).</summary>
    public string Directory { get; }

    /// <summary>Today's file.</summary>
    public string CurrentFile => Path.Combine(Directory, $"editor-{_time.GetUtcNow():yyyy-MM-dd}.log");

    public override bool IsThreadSafe => true;

    /// <summary>
    /// Adds a log in <paramref name="directory"/> to <see cref="Trace.Listeners"/> (once) and
    /// deletes logs older than the newest <see cref="KeptFiles"/>. Never throws; null when the
    /// folder can't be created.
    /// </summary>
    public static FileLog? Install(string directory, TimeProvider? time = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var log = new FileLog(directory, time);
            log.DeleteOldFiles();
            if (Trace.Listeners.OfType<FileLog>().FirstOrDefault(l => l.Directory == directory) is { } existing)
            {
                log.Dispose();
                return existing;
            }

            Trace.Listeners.Add(log);
            log.WriteLine($"Farming RPG Maker {FarmingRpgMaker.Updates.AppVersion.Current} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override void Write(string? message)
    {
        lock (_gate)
        {
            _pending += message;
        }
    }

    public override void WriteLine(string? message)
    {
        string line;
        lock (_gate)
        {
            line = _pending + message;
            _pending = null;
        }

        Append($"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}");
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter is null || Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
        {
            WriteLine($"[{eventType}] {message}");
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id, args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format ?? "", args) : format);

    private void Append(string text)
    {
        lock (_gate)
        {
            try
            {
                var file = CurrentFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length >= MaxFileBytes)
                {
                    return;
                }

                File.AppendAllText(file, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it.
            }
        }
    }

    private void DeleteOldFiles()
    {
        var old = System.IO.Directory.EnumerateFiles(Directory, "editor-*.log")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(KeptFiles);
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again next launch.
            }
        }
    }
}
