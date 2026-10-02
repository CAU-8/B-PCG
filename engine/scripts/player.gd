class_name Player
extends CharacterBody3D
## 1인칭으로 날거나(노클립) 걷습니다. V 로 둘을 바꿉니다.
##
## 노클립(기본): 땅과 동굴 벽을 뚫고 바라보는 방향으로 납니다. WASD 로 움직이고, Space·E 로 오르고,
## Q·Ctrl 로 내립니다. Shift 를 누르면 fast_multiplier 배 빠르고, 마우스 휠로 속력을 바꿉니다.[br]
## 걷기: WASD 로 걷고 Space 로 뜁니다. 중력은 프로젝트 설정(physics/3d/default_gravity)을 따릅니다.[br]
## 화면을 누르면 마우스로 둘러보고, Esc 로 마우스를 놓습니다.

## 노클립이 켜지거나 꺼질 때 알립니다.
signal noclip_changed(on: bool)

## 노클립으로 시작할지.
@export var noclip: bool = true
## 걷는 속력 (m/s).
@export var walk_speed_m_s: float = 5.0
## 뛰어오를 때의 처음 위쪽 속력 (m/s).
@export var jump_speed_m_s: float = 4.5
## 나는 속력 (m/s). 마우스 휠로 fly_speed_step 배씩 바꿉니다.
@export var fly_speed_m_s: float = 25.0
@export var fly_speed_min_m_s: float = 1.0
@export var fly_speed_max_m_s: float = 3000.0
@export var fly_speed_step: float = 1.25
## Shift 를 누를 때 곱하는 배수.
@export var fast_multiplier: float = 5.0
## 마우스 한 픽셀에 도는 각 (rad).
@export var mouse_sensitivity_rad: float = 0.002
## 걷기에서 이 높이 (m) 아래로 떨어지면 처음 자리로 돌려놓습니다.
@export var fall_limit_y_m: float = -1000.0

var gravity_m_s2: float = ProjectSettings.get_setting("physics/3d/default_gravity")
## 전역 위치의 지면 높이 (엔진 Y, 없으면 NAN) 를 돌려주는 함수. main.gd 가 넣습니다.
## 걷기로 바꿀 때 땅속이면 지면 위로 올립니다.
var ground_height_at: Callable

var _spawn_position := Vector3.ZERO

@onready var head: Node3D = $Head
@onready var shape: CollisionShape3D = $Shape


func _ready() -> void:
	shape.disabled = noclip
	# main.gd 가 위치를 정한 다음 프레임에 처음 자리를 기억합니다.
	_remember_spawn.call_deferred()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		if noclip and event.button_index == MOUSE_BUTTON_WHEEL_UP:
			fly_speed_m_s = minf(fly_speed_m_s * fly_speed_step, fly_speed_max_m_s)
		elif noclip and event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			fly_speed_m_s = maxf(fly_speed_m_s / fly_speed_step, fly_speed_min_m_s)
		elif event.button_index == MOUSE_BUTTON_LEFT:
			Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	elif event.is_action_pressed("ui_cancel"):
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	elif event.is_action_pressed("toggle_noclip"):
		set_noclip(not noclip)
	elif event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		rotate_y(-event.relative.x * mouse_sensitivity_rad)
		head.rotate_x(-event.relative.y * mouse_sensitivity_rad)
		head.rotation.x = clampf(head.rotation.x, -PI * 0.49, PI * 0.49)


func _physics_process(delta: float) -> void:
	if noclip:
		_fly(delta)
		return
	if not is_on_floor():
		velocity.y -= gravity_m_s2 * delta
	elif Input.is_action_just_pressed("jump"):
		velocity.y = jump_speed_m_s

	var input := Input.get_vector("move_left", "move_right", "move_forward", "move_back")
	var direction := (transform.basis * Vector3(input.x, 0.0, input.y)).normalized()
	velocity.x = direction.x * walk_speed_m_s
	velocity.z = direction.z * walk_speed_m_s
	move_and_slide()

	if global_position.y < fall_limit_y_m:
		global_position = _spawn_position
		velocity = Vector3.ZERO


## 노클립을 켜거나 끕니다. 끌 때 땅속이면 지면 위로 올립니다.
func set_noclip(on: bool) -> void:
	noclip = on
	velocity = Vector3.ZERO
	shape.disabled = on
	if not on:
		lift_above_ground()
	noclip_changed.emit(on)


## 발이 지면 아래(또는 지면에 거의 붙어)면 지면 위 0.5 m 로 올리고 아래로 가던 속도를 없앱니다.
## 지형 높이맵이 바뀔 때(프랙탈 디테일) 걷는 플레이어가 새 땅속에 묻히지 않게 씁니다.
## 올렸으면 true.
func lift_above_ground() -> bool:
	if not ground_height_at.is_valid():
		return false
	var ground: float = ground_height_at.call(global_position)
	if not is_finite(ground) or global_position.y >= ground + 0.1:
		return false
	global_position.y = ground + 0.5
	velocity.y = maxf(velocity.y, 0.0)
	return true


## 지금 나는 속력 (Shift 포함, m/s). 걷기면 걷는 속력.
func current_speed_m_s() -> float:
	if not noclip:
		return walk_speed_m_s
	return fly_speed_m_s * (fast_multiplier if Input.is_action_pressed("move_fast") else 1.0)


## 카메라(눈)가 eye 에 오고 target 을 보게 옮깁니다.
func look_from(eye: Vector3, target: Vector3) -> void:
	global_position = eye - Vector3(0.0, head.position.y, 0.0)
	var d := target - eye
	if d.length() < 1e-6:
		return
	rotation.y = atan2(-d.x, -d.z)
	head.rotation.x = clampf(atan2(d.y, Vector2(d.x, d.z).length()), -PI * 0.49, PI * 0.49)


## 카메라 위치 (전역).
func eye_position() -> Vector3:
	return head.global_position


func _fly(delta: float) -> void:
	var input := Input.get_vector("move_left", "move_right", "move_forward", "move_back")
	var direction := head.global_basis * Vector3(input.x, 0.0, input.y)
	direction += Vector3.UP * Input.get_axis("fly_down", "fly_up")
	if direction.length_squared() > 1.0:
		direction = direction.normalized()
	global_position += direction * current_speed_m_s() * delta
	velocity = Vector3.ZERO


func _remember_spawn() -> void:
	_spawn_position = global_position
