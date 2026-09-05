using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace RavaCast.UI;

public sealed class UiSharedService
{
    public NoopFont UidFont { get; } = new();
    public IDisposable? BeginThemed() => null;

    public bool IconTextButton(FontAwesomeIcon icon, string text, float? width = null, bool isInPopup = false)
        => ImGui.Button(text, width is > 0 ? new Vector2(width.Value, 0f) : Vector2.Zero);

    public void BigText(string text, Vector4 color)
    {
        var font = ImGui.GetFont();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.GetFontSize() * 1.28f;
        ImGui.GetWindowDrawList().AddText(font, size, pos, ImGui.GetColorU32(color), text);
        ImGui.Dummy(new Vector2(ImGui.CalcTextSize(text).X, size + 2f * ImGuiHelpers.GlobalScale));
    }

    public static void AttachToolTip(string text)
    {
        if (!ImGui.IsItemHovered() || string.IsNullOrWhiteSpace(text)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void TextWrapped(string text, float wrapPos = 0f)
    {
        ImGui.PushTextWrapPos(wrapPos > 0 ? wrapPos : 0f);
        ImGui.TextUnformatted(text ?? string.Empty);
        ImGui.PopTextWrapPos();
    }

    public sealed class NoopFont
    {
        public IDisposable Push() => new NoopScope();
    }

    private sealed class NoopScope : IDisposable { public void Dispose() { } }
}
