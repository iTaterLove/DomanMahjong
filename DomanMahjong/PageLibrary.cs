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

    private static IReadOnlyList<PageBlock> Parse(string raw)
    {
        var blocks = new List<PageBlock>();
        var text = new StringBuilder();

        void FlushText()
        {
            var t = text.ToString().Trim('\n');
            if (t.Length > 0) blocks.Add(new TextBlock(t));
            text.Clear();
        }

                foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
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

        FlushText();
        return blocks;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
 
    public void Dispose() => watcher.Dispose();
}
