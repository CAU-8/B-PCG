using System;
using System.Globalization;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 화면 속도와 그리기 비용을 읽습니다. 회랑 HUD 의 속도 줄(<see cref="Hud"/>)과 그리기 비용 재기 장면(Tests/perf.tscn)이 같이 씁니다.
/// </summary>
/// <remarks>
/// 화면 속도(FPS)는 1초에 새로 그린 화면 수입니다. 수직 동기화(화면 주사율에 맞춰 다음 화면을 기다리는 설정)가 켜져 있으면
/// 그보다 빨리 그릴 수 있어도 주사율(맥북 60 Hz)에서 멈춥니다. 그래서 GPU 가 화면 한 장을 그리는 데 쓴 시간도 함께 읽습니다.
/// 이 시간이 16.7 ms 보다 짧으면 60 fps 를 지킬 여유가 있습니다.
/// 삼각형 수는 Godot 가 지난 화면에서 실제로 그린 수입니다. 화면에 그린 수와, 해가 드리우는 그림자 지도를 만들려고
/// 다시 그린 수를 따로 셉니다.
/// </remarks>
public static class FrameStats
{
    /// <summary>화면 한 장의 그리기 비용.</summary>
    /// <param name="Fps">지난 1초 동안 그린 화면 수.</param>
    /// <param name="GpuMs">GPU 가 지난 화면 한 장을 그리는 데 쓴 시간 (ms). 재기를 켜기 전에는 0.</param>
    /// <param name="Triangles">화면에 그린 삼각형 수 (그림자 제외).</param>
    /// <param name="ShadowTriangles">그림자 지도에 그린 삼각형 수.</param>
    /// <param name="DrawCalls">GPU 에 그리기를 맡긴 횟수 (화면 + 그림자).</param>
    public readonly record struct Sample(double Fps, double GpuMs, long Triangles, long ShadowTriangles, long DrawCalls);

    /// <summary>창(뷰포트)의 GPU 시간 재기를 켭니다. 켠 뒤 몇 장 지나야 값이 나옵니다.</summary>
    public static void EnableGpuTiming(Viewport viewport) =>
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);

    /// <summary>지난 화면의 그리기 비용을 읽습니다.</summary>
    public static Sample Read(Viewport viewport)
    {
        Rid rid = viewport.GetViewportRid();
        return new Sample(
            Godot.Engine.GetFramesPerSecond(),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid),
            viewport.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame),
            viewport.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame),
            viewport.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame)
                + viewport.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame));
    }

    /// <summary>HUD 첫 줄. 예: "화면 속도 60 fps (GPU 한 장 9.8 ms) · 삼각형 412만 + 그림자 96만".</summary>
    public static string HudLine(Sample s)
    {
        string gpu = s.GpuMs > 0.0 ? $" (GPU 한 장 {s.GpuMs.ToString("F1", CultureInfo.InvariantCulture)} ms)" : "";
        return $"화면 속도 {Math.Round(s.Fps).ToString("F0", CultureInfo.InvariantCulture)} fps{gpu}"
            + $" · 삼각형 {Man(s.Triangles)} + 그림자 {Man(s.ShadowTriangles)}";
    }

    /// <summary>큰 수를 '만' 단위로 줄여 씁니다. 9,999 까지는 그대로, 100만 이상은 정수 만, 그 사이는 소수 한 자리 만.</summary>
    public static string Man(long n)
    {
        if (Math.Abs(n) < 10_000)
        {
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }
        double man = n / 10_000.0;
        string format = Math.Abs(man) >= 100.0 ? "N0" : "0.#";
        return man.ToString(format, CultureInfo.InvariantCulture) + "만";
    }
}
