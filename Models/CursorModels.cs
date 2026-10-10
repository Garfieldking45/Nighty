using System.Text.Json.Serialization;
using Nighty.Mvvm;

namespace Nighty.Models;

public enum CursorShape { Plus, Split, X, Dot, Ring, Square, Diamond, Triangle, Arrow, Star, Line }

/// <summary>One shape in a cursor, drawn on a 64 x 64 grid. Layers are drawn first to last.</summary>
public sealed class CursorLayer : ObservableObject
{
    private CursorShape _shape = CursorShape.Plus;
    private string _color = "#FFFFFF", _outlineColor = "#000000", _glowColor = "#FFFFFF";
    private double _size = 36, _thickness = 4, _gap, _rotation, _opacity = 100, _outlineWidth = 1.5, _glow, _ox, _oy;
    private bool _outline = true, _filled = true;

    public CursorShape Shape { get => _shape; set => Set(ref _shape, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    /// <summary>Length (or diameter) in grid units, 2-60.</summary>
    public double Size { get => _size; set => Set(ref _size, Math.Clamp(value, 2, 60)); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 1, 30)); }
    /// <summary>Empty space around the centre of a plus.</summary>
    public double Gap { get => _gap; set => Set(ref _gap, Math.Clamp(value, 0, 28)); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, Math.Clamp(value, -180, 180)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 5, 100)); }
    public bool Outline { get => _outline; set => Set(ref _outline, value); }
    public string OutlineColor { get => _outlineColor; set => Set(ref _outlineColor, value); }
    public double OutlineWidth { get => _outlineWidth; set => Set(ref _outlineWidth, Math.Clamp(value, 0, 6)); }
    public double GlowSize { get => _glow; set => Set(ref _glow, Math.Clamp(value, 0, 14)); }
    public string GlowColor { get => _glowColor; set => Set(ref _glowColor, value); }
    public double OffsetX { get => _ox; set => Set(ref _ox, Math.Clamp(value, -30, 30)); }
    public double OffsetY { get => _oy; set => Set(ref _oy, Math.Clamp(value, -30, 30)); }
    /// <summary>Solid fill (squares, diamonds, triangles, stars) or just the outline line.</summary>
    public bool Filled { get => _filled; set => Set(ref _filled, value); }

    [JsonIgnore] public string Title => Shape.ToString();
    public CursorLayer Clone() => (CursorLayer)MemberwiseClone();
}

public enum CursorKind { Builder, Image }
public enum RecordSource { Screen, RobloxWindow }

/// <summary>Instant Replay and screen recording preferences.</summary>
public sealed class RecordSettings : ObservableObject
{
    private bool _replay;
    private int _clipSeconds = 30, _fps = 30, _res = 720, _quality = 1;
    private RecordSource _source = RecordSource.Screen;
    private bool _cursor = true, _note = true, _sound = true, _sort;
    private string _folder = "", _noteText = "Clip saved";
    private int _clipVk = 0x79, _clipMods, _recVk = 0x7B, _recMods, _replayVk = 0x77, _replayMods;

    /// <summary>Instant Replay is switched on again at the next start.</summary>
    public bool ReplayOnAtStart { get => _replay; set => Set(ref _replay, value); }
    /// <summary>How far back a saved clip goes, in seconds (10-120).</summary>
    public int ClipSeconds { get => _clipSeconds; set => Set(ref _clipSeconds, Math.Clamp(value, 10, 120)); }
    public int Fps { get => _fps; set => Set(ref _fps, value is 30 or 60 ? value : 30); }
    /// <summary>Video height in pixels; 0 keeps the native size.</summary>
    public int Resolution { get => _res; set => Set(ref _res, value); }
    /// <summary>0 light, 1 balanced, 2 high.</summary>
    public int Quality { get => _quality; set => Set(ref _quality, Math.Clamp(value, 0, 2)); }
    public RecordSource Source { get => _source; set => Set(ref _source, value); }
    public bool ShowCursor { get => _cursor; set => Set(ref _cursor, value); }
    public bool OnScreenNote { get => _note; set => Set(ref _note, value); }
    public string NoteText { get => _noteText; set => Set(ref _noteText, string.IsNullOrWhiteSpace(value) ? "Clip saved" : value.Trim()); }
    public bool ClipSound { get => _sound; set => Set(ref _sound, value); }
    public bool SortByApp { get => _sort; set => Set(ref _sort, value); }
    /// <summary>Where clips and recordings go. Empty = Videos\Nighty.</summary>
    public string Folder { get => _folder; set => Set(ref _folder, value ?? ""); }
    public int ClipVk { get => _clipVk; set => Set(ref _clipVk, value); }
    public int ClipMods { get => _clipMods; set => Set(ref _clipMods, value); }
    public int RecordVk { get => _recVk; set => Set(ref _recVk, value); }
    public int RecordMods { get => _recMods; set => Set(ref _recMods, value); }
    public int ReplayVk { get => _replayVk; set => Set(ref _replayVk, value); }
    public int ReplayMods { get => _replayMods; set => Set(ref _replayMods, value); }
}

/// <summary>A cursor in the library: drawn from layers in the builder, or made from a picture.</summary>
public sealed class CursorSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My cursor";
    public string Description { get; set; } = "";
    public CursorKind Kind { get; set; } = CursorKind.Builder;
    public List<CursorLayer> Layers { get; set; } = new();
    /// <summary>File name inside the cursors folder for picture cursors.</summary>
    public string? ImageFile { get; set; }
    /// <summary>The click point is the centre (crosshairs) instead of the top-left corner (arrows).</summary>
    public bool CenterHotspot { get; set; } = true;
    [JsonIgnore] public bool IsBuiltIn { get; set; }

    public CursorSpec Clone() => new()
    {
        Name = Name, Description = Description, Kind = Kind, ImageFile = ImageFile, CenterHotspot = CenterHotspot,
        Layers = Layers.Select(l => l.Clone()).ToList(),
    };
}

public sealed class RobloxSettings : ObservableObject
{
    private string _customFolder = "", _cursorId = "", _fontId = "", _size = "medium";
    private double _brightness;
    private bool _firstPerson = true, _keep = true;
    /// <summary>A Roblox (or Bloxstrap, Fishstrap...) folder chosen by hand when it isn't found automatically.</summary>
    public string CustomFolder { get => _customFolder; set => Set(ref _customFolder, value ?? ""); }
    /// <summary>The cursor currently applied to Roblox (empty = Roblox's own).</summary>
    public string CursorId { get => _cursorId; set => Set(ref _cursorId, value ?? ""); }
    /// <summary>small, medium or large.</summary>
    public string CursorSize { get => _size; set => Set(ref _size, value ?? "medium"); }
    /// <summary>-100 (darker) to +100 (brighter), applied to every cursor.</summary>
    public double CursorBrightness { get => _brightness; set => Set(ref _brightness, Math.Clamp(Math.Round(value), -100, 100)); }
    /// <summary>Also use the cursor in shift lock and first person.</summary>
    public bool FirstPerson { get => _firstPerson; set => Set(ref _firstPerson, value); }
    /// <summary>Put the cursor and font back after Roblox updates.</summary>
    public bool KeepAfterUpdates { get => _keep; set => Set(ref _keep, value); }
    /// <summary>Id (file path) of the font applied to Roblox, empty = Roblox's own.</summary>
    public string FontId { get => _fontId; set => Set(ref _fontId, value ?? ""); }
}
