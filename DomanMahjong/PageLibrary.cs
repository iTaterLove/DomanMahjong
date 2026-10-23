using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DomanMahjong;

public abstract record PageBlock;
public sealed record TextBlock(string Text) : PageBlock;
public sealed record ImageBlock(string FileName, int? Width) : PageBlock;
public sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : PageBlock;

public sealed record Page(string Name, IReadOnlyList<PageBlock> Blocks);

public sealed class PageLibrary : IDisposable
{
    private readonly FileSystemWatcher watcher;

    private volatile bool dirty = true;
    private DateTime retryAt = DateTime.MinValue;
    private IReadOnlyList<Page> pages = Array.Empty<Page>();

    public string Folder { get; }
    public IReadOnlyList<Page> Pages => pages;

    public PageLibrary(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);

        watcher = new FileSystemWatcher(folder, "*.txt") {EnableRaisingEvents = true};
        watcher.Created += (_, _) => dirty = true;
        watcher.Changed += (_, _) => dirty = true;
        watcher.Deleted += (_, _) => dirty = true;
        watcher.Renamed += (_, _) => dirty = true;
    }
    
    public void Update()
    {
        if (!dirty || DateTime.UtcNow < retryAt) return;
        dirty = false;
        Load();
    }

    private void Load()
    {
        try
        {
            pages = Directory.EnumerateFiles(Folder, "*.txt")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new Page(Path.GetFileNameWithoutExtension(path), Parse(ReadShared(path))))
                .ToList();
        }
        catch (IOException ex)
        {
            // Typically an editor is mid-save. Keep the old list and try again in a second.
            Plugin.Log.Debug(ex, "Pages reload failed, retrying");
            dirty = true;
            retryAt = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private static readonly Regex ImageLine =
        new(@"^\s*\[img:\s*([^\]|]+?)\s*(?:\|\s*(\d+)\s*)?\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DividerCell = new(@"^:?-+:?$", RegexOptions.Compiled);

    private static IReadOnlyList<PageBlock> Parse(string raw)
    {
        var blocks = new List<PageBlock>();
        var text = new StringBuilder();
        var tableLines = new List<string>();

        void FlushText()
        {
            var t = text.ToString().Trim('\n');
            if (t.Length > 0) blocks.Add(new TextBlock(t));
            text.Clear();
        }

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            blocks.Add(ParseTable(tableLines));
            tableLines.Clear();
        }


        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            // Consecutive lines that start with | form one table.
            if (line.TrimStart().StartsWith('|'))
            {
                FlushText();
                tableLines.Add(line);
                continue;
            }
 
            FlushTable();
 
            var m = ImageLine.Match(line);
            if (m.Success)
            {
                FlushText();
                int? width = int.TryParse(m.Groups[2].Value, out var w) ? w : null;
                blocks.Add(new ImageBlock(m.Groups[1].Value, width));
            }
            else
            {
                text.Append(line).Append('\n');
            }
        }


        FlushTable();
        FlushText();
        return blocks;
    }

    private static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }
 
    private static TableBlock ParseTable(List<string> lines)
    {
        var rows = lines.Select(SplitRow).ToList();
        IReadOnlyList<string> header = Array.Empty<string>();
 
        // A divider row (|---|---|) directly under the first row marks that first row as the header.
        if (rows.Count >= 2 && rows[1].All(c => DividerCell.IsMatch(c)))
        {
            header = rows[0];
            rows.RemoveRange(0, 2);
        }
        else
        {
            rows.RemoveAll(r => r.All(c => DividerCell.IsMatch(c)));   // ignore stray divider rows
        }
 
        return new TableBlock(header, rows.Cast<IReadOnlyList<string>>().ToList());
    }


    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
 
    public void Dispose() => watcher.Dispose();
}
