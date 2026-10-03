using System;
using System.Collections.Generic;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 재질 부피(strata.u8 + strata.json)와 그 윗면(strata_top)을 읽어 셰이더용 텍스처로 바꿉니다.
/// </summary>
/// <remarks>
/// strata.u8 은 uint8, 배치는 row_layer_col 입니다: 번호 = (행 · 층 수 + 층) · 열 수 + 열.
/// 행이 늘면 +Z (북 → 남), 열이 늘면 +X, 층은 윗면에서 아래로 dy 씩 내려갑니다.
/// 층 k 의 가운데 Y = (strata_top 값 + strata_top.origin.y) − (k + 0.5) · dy.
/// 값: 0..11 암석(geology.rocks), 254 물, 255 공기(동굴).
/// 한 행(층 × 열)이 바이트로 이어져 있어 3D 텍스처의 한 장(가로 = 열, 세로 = 층)이 됩니다.
/// </remarks>
public sealed class StrataVolume
{
    public const string Format = "uint8";
    public const string Layout = "row_layer_col";
    public const int WaterId = 254;
    public const int AirId = 255;

    /// <summary>3D 텍스처 한 변의 상한 (Metal·Vulkan 이 보장하는 값).</summary>
    public const int MaxTexture3DSize = 2048;

    /// <summary>재질 번호 → 한국어 이름.</summary>
    public static readonly Dictionary<string, string> KoreanNames = new()
    {
        ["alluvium"] = "충적층",
        ["sandstone"] = "사암",
        ["shale"] = "셰일",
        ["limestone"] = "석회암",
        ["granite"] = "화강암",
        ["volcanic"] = "화산암",
        ["slate"] = "점판암",
        ["schist"] = "편암",
        ["gneiss"] = "편마암",
        ["marble"] = "대리암",
        ["quartzite"] = "규암",
        ["soil"] = "흙",
        ["water"] = "물",
        ["air"] = "공기(동굴)",
    };

    /// <summary>행 수 (Z), 층 수 (아래로), 열 수 (X).</summary>
    public int Rows { get; private set; }

    public int Layers { get; private set; }

    public int Cols { get; private set; }

    /// <summary>(dx, dy, dz) m.</summary>
    public Vector3 Spacing { get; private set; } = Vector3.Zero;

    /// <summary>0번 열의 X 와 0번 행의 Z (m).</summary>
    public Vector2 OriginXZ { get; private set; } = Vector2.Zero;

    /// <summary>재질 번호 (바이트 그대로).</summary>
    public byte[] Data { get; private set; } = [];

    /// <summary>윗면 높이맵 (strata_top).</summary>
    public HeightmapLoader Top { get; private set; } = new();

    /// <summary>재질 번호 → 영어 이름, 색.</summary>
    public Dictionary<int, string> Names { get; } = [];

    public Dictionary<int, Color> Colors { get; } = [];

    /// <summary>셰이더용 텍스처.</summary>
    public ImageTexture3D? Texture { get; private set; }

    public ImageTexture? TopTexture { get; private set; }

    public ImageTexture? Palette { get; private set; }

    public string ErrorMessage { get; private set; } = "";

    /// <summary>dir 아래 strata.json, strata.u8, strata_top.* 을 읽습니다.</summary>
    public static StrataVolume LoadDir(string dir)
    {
        var volume = new StrataVolume();
        volume.Read(dir);
        return volume;
    }

    public bool IsValid() => ErrorMessage.Length == 0;

    /// <summary>구운 깊이 (m).</summary>
    public float DepthM() => Layers * Spacing.Y;

    /// <summary>셰이더 재질에 strata.gdshaderinc 의 uniform 을 채웁니다.</summary>
    public void ApplyTo(ShaderMaterial material)
    {
        material.SetShaderParameter("has_strata", true);
        material.SetShaderParameter("strata", Texture!);
        material.SetShaderParameter("strata_top", TopTexture!);
        material.SetShaderParameter("palette", Palette!);
        material.SetShaderParameter("strata_origin", new Vector3(OriginXZ.X, Top.Origin.Y, OriginXZ.Y));
        material.SetShaderParameter("strata_spacing", Spacing);
        material.SetShaderParameter("strata_size", new Vector3(Cols, Layers, Rows));
    }

    /// <summary>
    /// 전역 위치의 재질 번호. 부피 밖(옆·아래)이면 -1, 윗면 위면 AirId.
    /// 셰이더와 달리 가장 가까운 기둥의 윗면을 써서 굽기 값과 정확히 같습니다.
    /// </summary>
    public int IdAt(Vector3 p)
    {
        if (!IsValid())
        {
            return -1;
        }
        // GDScript roundi 와 같이 반올림은 0 에서 먼 쪽으로 합니다.
        int col = (int)Math.Round((p.X - OriginXZ.X) / (double)Spacing.X, MidpointRounding.AwayFromZero);
        int row = (int)Math.Round((p.Z - OriginXZ.Y) / (double)Spacing.Z, MidpointRounding.AwayFromZero);
        if (col < 0 || row < 0 || col >= Cols || row >= Rows)
        {
            return -1;
        }
        double topY = (double)Top.Heights[(row * Cols) + col] + Top.Origin.Y;
        int layer = (int)Math.Floor((topY - p.Y) / Spacing.Y);
        if (layer < 0)
        {
            return AirId;
        }
        if (layer >= Layers)
        {
            return -1;
        }
        return Data[(((row * Layers) + layer) * Cols) + col];
    }

    /// <summary>재질 번호의 한국어 이름 (모르면 "번호 N").</summary>
    public string NameOf(int id)
    {
        string english = Names.GetValueOrDefault(id, "");
        return KoreanNames.TryGetValue(english, out string? korean) ? korean : $"번호 {id}";
    }

    private void Read(string dir)
    {
        string jsonPath = dir.PathJoin("strata.json");
        string rawPath = dir.PathJoin("strata.u8");
        if (!Files.Exists(jsonPath) || !Files.Exists(rawPath))
        {
            ErrorMessage = $"재질 부피 파일이 없습니다: {rawPath}";
            return;
        }
        OrderedDictionary<string, object?>? meta = Json.ReadObject(jsonPath);
        if (meta is null)
        {
            ErrorMessage = $"{jsonPath} 를 JSON 으로 읽지 못했습니다";
            return;
        }
        if (Json.S(Json.Get(meta, "format")) != Format || Json.S(Json.Get(meta, "layout")) != Layout)
        {
            ErrorMessage = $"지원하지 않는 재질 부피 형식입니다: {Json.S(Json.Get(meta, "format"))}, {Json.S(Json.Get(meta, "layout"))}";
            return;
        }
        List<object?> shape = Json.Arr(meta["shape"])!;
        Rows = Json.I(shape[0]);
        Layers = Json.I(shape[1]);
        Cols = Json.I(shape[2]);
        OrderedDictionary<string, object?> sp = Json.Obj(meta["spacing_m"])!;
        Spacing = new Vector3(Json.F(sp["x"]), Json.F(sp["y"]), Json.F(sp["z"]));
        List<object?> o = Json.Arr(meta["origin_xz"])!;
        OriginXZ = new Vector2(Json.F(o[0]), Json.F(o[1]));
        if (Math.Max(Rows, Math.Max(Layers, Cols)) > MaxTexture3DSize)
        {
            ErrorMessage = $"재질 부피 {Cols} × {Layers} × {Rows} 가 3D 텍스처 한도 {MaxTexture3DSize} 를 넘습니다";
            return;
        }

        Data = Files.Bytes(rawPath);
        if (Data.Length != Rows * Layers * Cols)
        {
            ErrorMessage = $"{rawPath} 크기 {Data.Length} 가 {Rows} × {Layers} × {Cols} 와 다릅니다";
            return;
        }
        Top = HeightmapLoader.LoadStem(dir.PathJoin(Json.Str(meta, "top_stem", "strata_top")));
        if (!Top.IsValid())
        {
            ErrorMessage = "재질 부피 윗면을 읽지 못했습니다: " + Top.ErrorMessage;
            return;
        }
        if (Top.Width != Cols || Top.Height != Rows)
        {
            ErrorMessage = $"윗면 {Top.Width} × {Top.Height} 가 재질 부피 열 × 행 {Cols} × {Rows} 와 다릅니다";
            return;
        }

        foreach (object? item in Json.Arr(Json.Get(meta, "legend")) ?? [])
        {
            OrderedDictionary<string, object?> entry = Json.Obj(item)!;
            int id = Json.I(entry["id"]);
            Names[id] = Json.S(entry["name"]);
            List<object?> rgb = Json.Arr(entry["rgb"])!;
            Colors[id] = Color.Color8((byte)Json.I(rgb[0]), (byte)Json.I(rgb[1]), (byte)Json.I(rgb[2]));
        }
        BuildTextures();
    }

    private void BuildTextures()
    {
        int sliceBytes = Layers * Cols;
        var images = new Godot.Collections.Array<Image>();
        for (int row = 0; row < Rows; row++)
        {
            byte[] slice = Data.AsSpan(row * sliceBytes, sliceBytes).ToArray();
            images.Add(Image.CreateFromData(Cols, Layers, false, Image.Format.R8, slice));
        }
        Texture = new ImageTexture3D();
        Error err = Texture.Create(Image.Format.R8, Cols, Layers, Rows, false, images);
        if (err != Error.Ok)
        {
            ErrorMessage = $"3D 텍스처를 만들지 못했습니다 (오류 {err})";
            return;
        }
        TopTexture = ImageTexture.CreateFromImage(Image.CreateFromData(
            Top.Width, Top.Height, false, Image.Format.Rf, Files.FloatBytes(Top.Heights)));
        Image pal = Image.CreateEmpty(256, 1, false, Image.Format.Rgba8);
        foreach ((int id, Color c) in Colors)
        {
            pal.SetPixel(id, 0, c);
        }
        Palette = ImageTexture.CreateFromImage(pal);
    }
}
