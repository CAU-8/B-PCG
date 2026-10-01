extends CharacterBody3D
## 1인칭으로 걷고 뜁니다.
##
## WASD 로 걷고 Space 로 뜁니다. 화면을 누르면 마우스로 둘러보고, Esc 로 마우스를 놓습니다.
## 중력은 프로젝트 설정(physics/3d/default_gravity)을 따릅니다.

## 걷는 속력 (m/s).
@export var walk_speed_m_s: float = 5.0
## 뛰어오를 때의 처음 위쪽 속력 (m/s).
@export var jump_speed_m_s: float = 4.5
## 마우스 한 픽셀에 도는 각 (rad).
@export var mouse_sensitivity_rad: float = 0.002
## 이 높이 (m) 아래로 떨어지면 처음 자리로 돌려놓습니다.
@export var fall_limit_y_m: float = -1000.0

var gravity_m_s2: float = ProjectSettings.get_setting("physics/3d/default_gravity")

var _spawn_position := Vector3.ZERO

@onready var head: Node3D = $Head


func _ready() -> void:
	# main.gd 가 위치를 정한 다음 프레임에 처음 자리를 기억합니다.
	_remember_spawn.call_deferred()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	elif event.is_action_pressed("ui_cancel"):
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	elif event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		rotate_y(-event.relative.x * mouse_sensitivity_rad)
		head.rotate_x(-event.relative.y * mouse_sensitivity_rad)
		head.rotation.x = clampf(head.rotation.x, -PI * 0.49, PI * 0.49)


func _physics_process(delta: float) -> void:
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


func _remember_spawn() -> void:
	_spawn_position = global_position
