using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DomanMahjong.Windows;

public class MainWindow : Window, IDisposable
{
    private const float ZoomStep = 0.1f;
    private const float ZoomMin = 0.5f;
    private const float ZoomMax= 3.0f;

    private readonly PageLibrary library;
    private readonly string imageFolder;

    private float zoom = 1.0f;

    private string? selectedName;

    public MainWindow(PageLibrary library)
    : base("Mahjong Guide", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        IsOpen = true;
        RespectCloseHotkey = true;
        this.library = library;
        imageFolder = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory!.FullName, "Data");
    }

    public void Dispose() { }

    public override void Draw()
    {
        library.Update();
        var pages = library.Pages;
 
        if (pages.Count == 0)
        {
            ImGui.TextWrapped($"No pages found. Add .txt files to:\n{library.Folder}");
            return;
        }
 
        // 1) Navigation bar: one button per page.
        DrawNavBar();
        ImGui.Separator();
 
        // 2) Content area: shows whichever page is selected.
        DrawContent();
    }
    private void DrawNavBar()
    {
        var pages = library.Pages;
        var style = ImGui.GetStyle();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var used = 0f;
 
        for (var i = 0; i < pages.Count; i++)
        {
            var name = pages[i].Name;
            var w = ImGui.CalcTextSize(name).X + style.FramePadding.X * 2;
 
            // Keep the button on the current row if it fits, otherwise start a new row.
            if (used > 0 && used + style.ItemSpacing.X + w <= rowWidth)
            {
                ImGui.SameLine();
                used += style.ItemSpacing.X;
            }
            else
            {
                used = 0;
            }
 
            // Highlight the button of the page that is currently open.
            var isSelected = name == selectedName;
            if (isSelected)
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
 
            ImGui.PushID(i);
            if (ImGui.Button(name))
                selectedName = name;
            ImGui.PopID();
 
            if (isSelected)
                ImGui.PopStyleColor();
 
            used += w;
        }
    }

        private void DrawContent()
    {
        var pages = library.Pages;
 
        // Find the selected page by name; fall back to the first one if it is unset or the file was deleted.
        Page? page = null;
        foreach (var p in pages)
            if (p.Name == selectedName) { page = p; break; }
        page ??= pages[0];
        selectedName = page.Name;
 
        var io = ImGui.GetIO();
 
        // While Ctrl is held the wheel means zoom, so stop the child from also scrolling.
        var childFlags = io.KeyCtrl ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
 
        // Vector2.Zero = "fill whatever space is left under the nav bar".
        if (ImGui.BeginChild("##content", Vector2.Zero, false, childFlags))
        {
            HandleZoomInput(io);
 
            // Scale only the content, so the nav bar stays a constant size.
            ImGui.SetWindowFontScale(zoom);
 
            for (var i = 0; i < page.Blocks.Count; i++)
            {
                switch (page.Blocks[i])
                {
                    case TextBlock t:
                        ImGui.TextWrapped(t.Text);
                        break;
                    case ImageBlock img:
                        DrawImage(img);
                        break;
                    case TableBlock table:
                        DrawTable(table, i);
                        break;
                }
            }
  
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.EndChild();   // always call EndChild, even if BeginChild returned false
    }


    private static void DrawTable(TableBlock table, int index)
    {
        // Column count = the widest row (header included); short rows are padded with empty cells.
        var columns = table.Header.Count;
        foreach (var row in table.Rows)
            columns = Math.Max(columns, row.Count);
        if (columns == 0) return;
 
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
 
        // The ID must be unique per table on the page, so it includes the block's position.
        if (!ImGui.BeginTable($"##table{index}", columns, flags))
            return;   // EndTable is only called when BeginTable returned true
 
        if (table.Header.Count > 0)
        {
            for (var c = 0; c < columns; c++)
                ImGui.TableSetupColumn(c < table.Header.Count ? table.Header[c] : string.Empty);
            ImGui.TableHeadersRow();
        }
 
        foreach (var row in table.Rows)
        {
            ImGui.TableNextRow();
            for (var c = 0; c < columns; c++)
            {
                ImGui.TableNextColumn();
                ImGui.TextWrapped(c < row.Count ? row[c] : string.Empty);
            }
        }
 
        ImGui.EndTable();
    }

    private void DrawImage(ImageBlock img)
    {
        // GetFileName strips any folders, so a page can't point outside the Data folder.
        var path = Path.Combine(imageFolder, Path.GetFileName(img.FileName));
        var texture = Plugin.TextureProvider.GetFromFile(path);
 
        // Loading is asynchronous: false + no error = still loading, false + error = failed (e.g. missing file).
        if (!texture.TryGetWrap(out var wrap, out var error))
        {
            ImGui.TextDisabled(error == null ? "[loading image...]" : $"[image not found: {img.FileName}]");
            return;
        }
 
        // Start from the optional |width, apply zoom, then never exceed the width of the window.
        var size = wrap.Size;
        var scale = img.Width is > 0 ? img.Width.Value / size.X : 1f;
        scale *= zoom;
 
        var maxWidth = ImGui.GetContentRegionAvail().X;
        if (size.X * scale > maxWidth)
            scale = maxWidth / size.X;
 
        ImGui.Image(wrap.Handle, size * scale);
    }

    private void HandleZoomInput(ImGuiIOPtr io)
    {
        // Only react while the pointer is over this window, and never while typing in a text box.
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) || io.WantTextInput) return;
 
        if (io.KeyCtrl && io.MouseWheel != 0)
            SetZoom(zoom + Math.Sign(io.MouseWheel) * ZoomStep);
 
        if (ImGui.IsKeyPressed(ImGuiKey.Equal) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd))
            SetZoom(zoom + ZoomStep);
        if (ImGui.IsKeyPressed(ImGuiKey.Minus) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract))
            SetZoom(zoom - ZoomStep);
    }
 
    private void SetZoom(float value) => zoom = Math.Clamp(value, ZoomMin, ZoomMax);

}
