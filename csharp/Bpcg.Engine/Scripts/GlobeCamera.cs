using System;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 지구본 둘레를 도는 카메라. 지구본은 원점에 있고 반지름이 1 입니다.
/// </summary>
/// <remarks>
/// 왼쪽 끌기: 돌리기 (위아래는 ±89° 까지). 휠·트랙패드: 확대·축소 (지구본 중심에서 1.15 ~ 8 반지름,
/// 지표 위 높이를 일정 비율로 바꿈). Space: 느린 자동 회전 켜기·끄기. F: 히어로 유역으로 날아가기.
/// 끌지 않고 눌렀다 떼면 Clicked 신호를 냅니다. 패널 위의 마우스 입력은 패널이 먼저 받으므로 _UnhandledInput 으로 받습니다.
/// </remarks>
public partial class GlobeCamera : Camera3D
{
    /// <summary>끌지 않고 왼쪽 단추를 눌렀다 뗐을 때 (화면 좌표).</summary>
    [Signal]
    public delegate void ClickedEventHandler(Vector2 screenPosition);

    public const float MinDistance = 1.15f;
    public const float MaxDistance = 8.0f;
    public const float StartDistance = 3.4f;

    /// <summary>휠 한 칸에 지표 위 높이를 바꾸는 배율.</summary>
    public const float ZoomStep = 1.15f;

    /// <summary>이만큼(픽셀) 움직이면 클릭이 아니라 끌기로 봅니다.</summary>
    public const float DragThresholdPx = 4.0f;

    /// <summary>시작 거리에서 1 픽셀 끌 때 도는 각도. 가까울수록 천천히 돕니다.</summary>
    public const float RotateRadPerPx = 0.005f;

    public static readonly float MaxPitchRad = Mathf.DegToRad(89.0f);

    /// <summary>자동 회전 속도 (rad/s).</summary>
    public const float AutoRotateRadS = 0.08f;

    /// <summary>히어로 유역으로 날아가는 시간과 도착 거리.</summary>
    public const float FlyTimeS = 1.2f;
    public const float FlyDistance = 1.8f;

    /// <summary>카메라 방향: yaw 는 +Y 축 둘레 (0 = +Z 쪽에서 봄), pitch 는 위(+)·아래(-).</summary>
    public float YawRad { get; set; }

    public float PitchRad { get; set; } = Mathf.DegToRad(20.0f);

    /// <summary>지구본 중심에서의 거리 (지구본 반지름 배).</summary>
    public float Distance { get; set; } = StartDistance;

    public bool AutoRotate { get; set; }

    /// <summary>히어로 유역 방향 (지구본 좌표의 단위 벡터, 없으면 Zero). GlobeMain 이 넣습니다.</summary>
    public Vector3 HeroDirection { get; set; } = Vector3.Zero;

    private bool _pressed;
    private bool _dragged;
    private Vector2 _pressPosition = Vector2.Zero;
    private Tween? _tween;

    public override void _Ready()
    {
        Near = 0.002f;
        Far = 100.0f;
        ApplyOrbit();
    }

    public override void _Process(double delta)
    {
        if (AutoRotate && !_pressed)
        {
            YawRad = Mathf.Wrap(YawRad + (AutoRotateRadS * (float)delta), -Mathf.Pi, Mathf.Pi);
            ApplyOrbit();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            OnMouseButton(mb);
        }
        else if (@event is InputEventMouseMotion motion && _pressed)
        {
            if ((motion.ButtonMask & MouseButtonMask.Left) == 0)
            {
                // 패널 위에서 단추를 떼면 떼는 입력이 패널로 가므로 여기서 끌기를 끝냅니다.
                _pressed = false;
                return;
            }
            if (motion.Position.DistanceTo(_pressPosition) > DragThresholdPx)
            {
                _dragged = true;
            }
            if (_dragged)
            {
                RotateBy(motion.Relative);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventPanGesture pan)
        {
            ZoomBy(pan.Delta.Y);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMagnifyGesture mag)
        {
            ZoomBy(-(mag.Factor - 1.0f) * 8.0f);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.Space:
                    ToggleAutoRotate();
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.F:
                    FlyToHero();
                    GetViewport().SetInputAsHandled();
                    break;
            }
        }
    }

    /// <summary>단위 방향 → 그 방향에서 지구본을 보는 yaw, pitch (라디안). X = yaw, Y = pitch.</summary>
    public static Vector2 AnglesOf(Vector3 dir)
    {
        Vector3 d = dir.Normalized();
        return new Vector2(Mathf.Atan2(d.X, d.Z), Mathf.Asin(Mathf.Clamp(d.Y, -1.0f, 1.0f)));
    }

    /// <summary>yaw, pitch → 카메라가 있는 쪽의 단위 방향.</summary>
    public static Vector3 OrbitDirection(float yaw, float pitch) =>
        new(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));

    /// <summary>지금 yaw, pitch, distance 로 자리와 방향을 맞춥니다 (늘 지구본 중심을 봄).</summary>
    public void ApplyOrbit()
    {
        PitchRad = Mathf.Clamp(PitchRad, -MaxPitchRad, MaxPitchRad);
        Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
        Vector3 dir = OrbitDirection(YawRad, PitchRad);
        Transform = new Transform3D(Basis.LookingAt(-dir, Vector3.Up), dir * Distance);
    }

    /// <summary>화면에서 끈 만큼 돌립니다. 가까이 볼수록 천천히 돕니다.</summary>
    public void RotateBy(Vector2 relativePx)
    {
        StopTween();
        float speed = Mathf.Clamp((Distance - 1.0f) / (StartDistance - 1.0f), 0.04f, 2.0f);
        YawRad = Mathf.Wrap(YawRad - (relativePx.X * RotateRadPerPx * speed), -Mathf.Pi, Mathf.Pi);
        PitchRad += relativePx.Y * RotateRadPerPx * speed;
        ApplyOrbit();
    }

    /// <summary>steps &gt; 0 이면 멀어지고 &lt; 0 이면 가까워집니다. 지표 위 높이를 ZoomStep 배씩 바꿉니다.</summary>
    public void ZoomBy(float steps)
    {
        StopTween();
        float altitude = Math.Max(Distance - 1.0f, 1e-3f) * Mathf.Pow(ZoomStep, steps);
        Distance = Mathf.Clamp(1.0f + altitude, MinDistance, MaxDistance);
        ApplyOrbit();
    }

    public void ToggleAutoRotate() => AutoRotate = !AutoRotate;

    /// <summary>dir 쪽 하늘에서 지구본을 보도록 옮깁니다. durationS 가 0 이면 바로 옮깁니다.</summary>
    public void FlyTo(Vector3 dir, float durationS = FlyTimeS, float toDistance = FlyDistance)
    {
        if (dir == Vector3.Zero)
        {
            return;
        }
        StopTween();
        AutoRotate = false;
        Vector2 target = AnglesOf(dir);
        float startYaw = YawRad;
        float startPitch = PitchRad;
        float startDistance = Distance;
        // 짧은 쪽으로 돕니다.
        float endYaw = startYaw + Mathf.Wrap(target.X - startYaw, -Mathf.Pi, Mathf.Pi);
        float endDistance = Mathf.Clamp(toDistance, MinDistance, MaxDistance);
        if (durationS <= 0.0f || !IsInsideTree())
        {
            YawRad = Mathf.Wrap(endYaw, -Mathf.Pi, Mathf.Pi);
            PitchRad = target.Y;
            Distance = endDistance;
            ApplyOrbit();
            return;
        }
        void Step(float t)
        {
            YawRad = Mathf.Lerp(startYaw, endYaw, t);
            PitchRad = Mathf.Lerp(startPitch, target.Y, t);
            Distance = Mathf.Lerp(startDistance, endDistance, t);
            ApplyOrbit();
        }
        _tween = CreateTween();
        _tween.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _tween.TweenMethod(Callable.From<float>(Step), 0.0f, 1.0f, durationS);
    }

    public void FlyToHero() => FlyTo(HeroDirection);

    /// <summary>날아가는 중인지.</summary>
    public bool IsFlying() => _tween is not null && _tween.IsValid() && _tween.IsRunning();

    private void OnMouseButton(InputEventMouseButton e)
    {
        switch (e.ButtonIndex)
        {
            case MouseButton.Left:
                if (e.Pressed)
                {
                    _pressed = true;
                    _dragged = false;
                    _pressPosition = e.Position;
                }
                else if (_pressed)
                {
                    _pressed = false;
                    if (!_dragged)
                    {
                        EmitSignal(SignalName.Clicked, e.Position);
                    }
                }
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.WheelUp:
                if (e.Pressed)
                {
                    ZoomBy(e.Factor > 0.0f ? -e.Factor : -1.0f);
                }
                GetViewport().SetInputAsHandled();
                break;
            case MouseButton.WheelDown:
                if (e.Pressed)
                {
                    ZoomBy(e.Factor > 0.0f ? e.Factor : 1.0f);
                }
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void StopTween()
    {
        if (_tween is not null && _tween.IsValid())
        {
            _tween.Kill();
        }
        _tween = null;
    }
}
