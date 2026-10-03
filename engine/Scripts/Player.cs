using System;
using Godot;

namespace Bpcg.Engine;

/// <summary>
/// 1인칭으로 날거나(노클립) 걷습니다. V 로 둘을 바꿉니다.
/// </summary>
/// <remarks>
/// 노클립(기본): 땅과 동굴 벽을 뚫고 바라보는 방향으로 납니다. WASD 로 움직이고, Space·E 로 오르고,
/// Q·Ctrl 로 내립니다. Shift 를 누르면 FastMultiplier 배 빠르고, 마우스 휠로 속력을 바꿉니다.
/// 걷기: WASD 로 걷고 Space 로 뜁니다. 중력은 프로젝트 설정(physics/3d/default_gravity)을 따릅니다.
/// 화면을 누르면 마우스로 둘러보고, Esc 로 마우스를 놓습니다.
/// </remarks>
public partial class Player : CharacterBody3D
{
    /// <summary>노클립이 켜지거나 꺼질 때 알립니다.</summary>
    [Signal]
    public delegate void NoclipChangedEventHandler(bool on);

    /// <summary>노클립으로 시작할지.</summary>
    [Export]
    public bool Noclip { get; set; } = true;

    /// <summary>걷는 속력 (m/s).</summary>
    [Export]
    public float WalkSpeedMS { get; set; } = 5.0f;

    /// <summary>뛰어오를 때의 처음 위쪽 속력 (m/s).</summary>
    [Export]
    public float JumpSpeedMS { get; set; } = 4.5f;

    /// <summary>나는 속력 (m/s). 마우스 휠로 FlySpeedStep 배씩 바꿉니다.</summary>
    [Export]
    public float FlySpeedMS { get; set; } = 25.0f;

    [Export]
    public float FlySpeedMinMS { get; set; } = 1.0f;

    [Export]
    public float FlySpeedMaxMS { get; set; } = 3000.0f;

    [Export]
    public float FlySpeedStep { get; set; } = 1.25f;

    /// <summary>Shift 를 누를 때 곱하는 배수.</summary>
    [Export]
    public float FastMultiplier { get; set; } = 5.0f;

    /// <summary>마우스 한 픽셀에 도는 각 (rad).</summary>
    [Export]
    public float MouseSensitivityRad { get; set; } = 0.002f;

    /// <summary>걷기에서 이 높이 (m) 아래로 떨어지면 처음 자리로 돌려놓습니다.</summary>
    [Export]
    public float FallLimitYM { get; set; } = -1000.0f;

    /// <summary>
    /// 전역 위치의 지면 높이 (엔진 Y, 없으면 NaN) 를 돌려주는 함수. Main 이 넣습니다.
    /// 걷기로 바꿀 때 땅속이면 지면 위로 올립니다.
    /// </summary>
    public Func<Vector3, float>? GroundHeightAt { get; set; }

    public Node3D Head { get; private set; } = null!;

    public CollisionShape3D Shape { get; private set; } = null!;

    private float _gravityMS2;
    private Vector3 _spawnPosition = Vector3.Zero;

    public override void _Ready()
    {
        Head = GetNode<Node3D>("Head");
        Shape = GetNode<CollisionShape3D>("Shape");
        _gravityMS2 = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
        Shape.Disabled = Noclip;
        // Main 이 위치를 정한 다음 프레임에 처음 자리를 기억합니다.
        Callable.From(RememberSpawn).CallDeferred();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (Noclip && button.ButtonIndex == MouseButton.WheelUp)
            {
                FlySpeedMS = Math.Min(FlySpeedMS * FlySpeedStep, FlySpeedMaxMS);
            }
            else if (Noclip && button.ButtonIndex == MouseButton.WheelDown)
            {
                FlySpeedMS = Math.Max(FlySpeedMS / FlySpeedStep, FlySpeedMinMS);
            }
            else if (button.ButtonIndex == MouseButton.Left)
            {
                Input.MouseMode = Input.MouseModeEnum.Captured;
            }
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (@event.IsActionPressed("toggle_noclip"))
        {
            SetNoclip(!Noclip);
        }
        else if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.Relative.X * MouseSensitivityRad);
            Head.RotateX(-motion.Relative.Y * MouseSensitivityRad);
            Vector3 r = Head.Rotation;
            r.X = Mathf.Clamp(r.X, -Mathf.Pi * 0.49f, Mathf.Pi * 0.49f);
            Head.Rotation = r;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Noclip)
        {
            Fly((float)delta);
            return;
        }
        Vector3 velocity = Velocity;
        if (!IsOnFloor())
        {
            velocity.Y -= _gravityMS2 * (float)delta;
        }
        else if (Input.IsActionJustPressed("jump"))
        {
            velocity.Y = JumpSpeedMS;
        }

        Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 direction = (Transform.Basis * new Vector3(input.X, 0.0f, input.Y)).Normalized();
        velocity.X = direction.X * WalkSpeedMS;
        velocity.Z = direction.Z * WalkSpeedMS;
        Velocity = velocity;
        MoveAndSlide();

        if (GlobalPosition.Y < FallLimitYM)
        {
            GlobalPosition = _spawnPosition;
            Velocity = Vector3.Zero;
        }
    }

    /// <summary>노클립을 켜거나 끕니다. 끌 때 땅속이면 지면 위로 올립니다.</summary>
    public void SetNoclip(bool on)
    {
        Noclip = on;
        Velocity = Vector3.Zero;
        Shape.Disabled = on;
        if (!on)
        {
            LiftAboveGround();
        }
        EmitSignal(SignalName.NoclipChanged, on);
    }

    /// <summary>
    /// 발이 지면 아래(또는 지면에 거의 붙어)면 지면 위 0.5 m 로 올리고 아래로 가던 속도를 없앱니다.
    /// 지형 높이맵이 바뀔 때(프랙탈 디테일) 걷는 플레이어가 새 땅속에 묻히지 않게 씁니다. 올렸으면 true.
    /// </summary>
    public bool LiftAboveGround()
    {
        if (GroundHeightAt is null)
        {
            return false;
        }
        float ground = GroundHeightAt(GlobalPosition);
        if (!float.IsFinite(ground) || GlobalPosition.Y >= ground + 0.1f)
        {
            return false;
        }
        Vector3 p = GlobalPosition;
        p.Y = ground + 0.5f;
        GlobalPosition = p;
        Vector3 v = Velocity;
        v.Y = Math.Max(v.Y, 0.0f);
        Velocity = v;
        return true;
    }

    /// <summary>지금 나는 속력 (Shift 포함, m/s). 걷기면 걷는 속력.</summary>
    public float CurrentSpeedMS()
    {
        if (!Noclip)
        {
            return WalkSpeedMS;
        }
        return FlySpeedMS * (Input.IsActionPressed("move_fast") ? FastMultiplier : 1.0f);
    }

    /// <summary>카메라(눈)가 eye 에 오고 target 을 보게 옮깁니다.</summary>
    public void LookFrom(Vector3 eye, Vector3 target)
    {
        GlobalPosition = eye - new Vector3(0.0f, Head.Position.Y, 0.0f);
        Vector3 d = target - eye;
        if (d.Length() < 1e-6f)
        {
            return;
        }
        Vector3 rot = Rotation;
        rot.Y = Mathf.Atan2(-d.X, -d.Z);
        Rotation = rot;
        Vector3 hr = Head.Rotation;
        hr.X = Mathf.Clamp(Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length()), -Mathf.Pi * 0.49f, Mathf.Pi * 0.49f);
        Head.Rotation = hr;
    }

    /// <summary>카메라 위치 (전역).</summary>
    public Vector3 EyePosition() => Head.GlobalPosition;

    private void Fly(float delta)
    {
        Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 direction = Head.GlobalBasis * new Vector3(input.X, 0.0f, input.Y);
        direction += Vector3.Up * Input.GetAxis("fly_down", "fly_up");
        if (direction.LengthSquared() > 1.0f)
        {
            direction = direction.Normalized();
        }
        GlobalPosition += direction * CurrentSpeedMS() * delta;
        Velocity = Vector3.Zero;
    }

    private void RememberSpawn() => _spawnPosition = GlobalPosition;
}
