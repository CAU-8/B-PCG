extends Camera3D
## 지구본 둘레를 도는 카메라. 지구본은 원점에 있고 반지름이 1 입니다.
##
## 왼쪽 끌기: 돌리기 (위아래는 ±89° 까지). 휠·트랙패드: 확대·축소 (지구본 중심에서 1.15 ~ 8 반지름,
## 지표 위 높이를 일정 비율로 바꿈). Space: 느린 자동 회전 켜기·끄기. F: 히어로 유역으로 날아가기.
## [br]끌지 않고 눌렀다 떼면 clicked 신호를 냅니다 (그 자리 값을 고정해 보기).
## 패널 위의 마우스 입력은 패널이 먼저 받으므로 _unhandled_input 으로 받습니다.

## 끌지 않고 왼쪽 단추를 눌렀다 뗐을 때 (화면 좌표).
signal clicked(screen_position: Vector2)

const MIN_DISTANCE := 1.15
const MAX_DISTANCE := 8.0
const START_DISTANCE := 3.4
## 휠 한 칸에 지표 위 높이를 바꾸는 배율.
const ZOOM_STEP := 1.15
## 이만큼(픽셀) 움직이면 클릭이 아니라 끌기로 봅니다.
const DRAG_THRESHOLD_PX := 4.0
## 시작 거리에서 1 픽셀 끌 때 도는 각도. 가까울수록 천천히 돕니다.
const ROTATE_RAD_PER_PX := 0.005
const MAX_PITCH_RAD := deg_to_rad(89.0)
## 자동 회전 속도 (rad/s).
const AUTO_ROTATE_RAD_S := 0.08
## 히어로 유역으로 날아가는 시간과 도착 거리.
const FLY_TIME_S := 1.2
const FLY_DISTANCE := 1.8

## 카메라 방향: yaw 는 +Y 축 둘레 (0 = +Z 쪽에서 봄), pitch 는 위(+)·아래(-).
var yaw_rad := 0.0
var pitch_rad := deg_to_rad(20.0)
## 지구본 중심에서의 거리 (지구본 반지름 배).
var distance := START_DISTANCE
var auto_rotate := false
## 히어로 유역 방향 (지구본 좌표의 단위 벡터, 없으면 ZERO). globe_main 이 넣습니다.
var hero_direction := Vector3.ZERO

var _pressed := false
var _dragged := false
var _press_position := Vector2.ZERO
var _tween: Tween


func _ready() -> void:
	near = 0.002
	far = 100.0
	apply_orbit()


func _process(delta: float) -> void:
	if auto_rotate and not _pressed:
		yaw_rad = wrapf(yaw_rad + AUTO_ROTATE_RAD_S * delta, -PI, PI)
		apply_orbit()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		_on_mouse_button(event)
	elif event is InputEventMouseMotion and _pressed:
		if not (event.button_mask & MOUSE_BUTTON_MASK_LEFT):
			# 패널 위에서 단추를 떼면 떼는 입력이 패널로 가므로 여기서 끌기를 끝냅니다.
			_pressed = false
			return
		if event.position.distance_to(_press_position) > DRAG_THRESHOLD_PX:
			_dragged = true
		if _dragged:
			rotate_by(event.relative)
			get_viewport().set_input_as_handled()
	elif event is InputEventPanGesture:
		zoom_by(event.delta.y)
		get_viewport().set_input_as_handled()
	elif event is InputEventMagnifyGesture:
		zoom_by(-(event.factor - 1.0) * 8.0)
		get_viewport().set_input_as_handled()
	elif event is InputEventKey and event.pressed and not event.echo:
		match event.physical_keycode:
			KEY_SPACE:
				toggle_auto_rotate()
				get_viewport().set_input_as_handled()
			KEY_F:
				fly_to_hero()
				get_viewport().set_input_as_handled()


## 단위 방향 → 그 방향에서 지구본을 보는 yaw, pitch (라디안). x = yaw, y = pitch.
static func angles_of(dir: Vector3) -> Vector2:
	var d := dir.normalized()
	return Vector2(atan2(d.x, d.z), asin(clampf(d.y, -1.0, 1.0)))


## yaw, pitch → 카메라가 있는 쪽의 단위 방향.
static func orbit_direction(yaw: float, pitch: float) -> Vector3:
	return Vector3(sin(yaw) * cos(pitch), sin(pitch), cos(yaw) * cos(pitch))


## 지금 yaw, pitch, distance 로 자리와 방향을 맞춥니다 (늘 지구본 중심을 봄).
func apply_orbit() -> void:
	pitch_rad = clampf(pitch_rad, -MAX_PITCH_RAD, MAX_PITCH_RAD)
	distance = clampf(distance, MIN_DISTANCE, MAX_DISTANCE)
	var dir := orbit_direction(yaw_rad, pitch_rad)
	transform = Transform3D(Basis.looking_at(-dir, Vector3.UP), dir * distance)


## 화면에서 끈 만큼 돌립니다. 가까이 볼수록 천천히 돕니다.
func rotate_by(relative_px: Vector2) -> void:
	_stop_tween()
	var speed := clampf((distance - 1.0) / (START_DISTANCE - 1.0), 0.04, 2.0)
	yaw_rad = wrapf(yaw_rad - relative_px.x * ROTATE_RAD_PER_PX * speed, -PI, PI)
	pitch_rad += relative_px.y * ROTATE_RAD_PER_PX * speed
	apply_orbit()


## steps > 0 이면 멀어지고 < 0 이면 가까워집니다. 지표 위 높이를 ZOOM_STEP 배씩 바꿉니다.
func zoom_by(steps: float) -> void:
	_stop_tween()
	var altitude := maxf(distance - 1.0, 1e-3) * pow(ZOOM_STEP, steps)
	distance = clampf(1.0 + altitude, MIN_DISTANCE, MAX_DISTANCE)
	apply_orbit()


func toggle_auto_rotate() -> void:
	auto_rotate = not auto_rotate


## dir 쪽 하늘에서 지구본을 보도록 옮깁니다. duration_s 가 0 이면 바로 옮깁니다.
func fly_to(dir: Vector3, duration_s: float = FLY_TIME_S,
		to_distance: float = FLY_DISTANCE) -> void:
	if dir == Vector3.ZERO:
		return
	_stop_tween()
	auto_rotate = false
	var target := angles_of(dir)
	var start_yaw := yaw_rad
	var start_pitch := pitch_rad
	var start_distance := distance
	# 짧은 쪽으로 돕니다.
	var end_yaw := start_yaw + wrapf(target.x - start_yaw, -PI, PI)
	var end_distance := clampf(to_distance, MIN_DISTANCE, MAX_DISTANCE)
	if duration_s <= 0.0 or not is_inside_tree():
		yaw_rad = wrapf(end_yaw, -PI, PI)
		pitch_rad = target.y
		distance = end_distance
		apply_orbit()
		return
	var step := func(t: float) -> void:
		yaw_rad = lerpf(start_yaw, end_yaw, t)
		pitch_rad = lerpf(start_pitch, target.y, t)
		distance = lerpf(start_distance, end_distance, t)
		apply_orbit()
	_tween = create_tween()
	_tween.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	_tween.tween_method(step, 0.0, 1.0, duration_s)


func fly_to_hero() -> void:
	fly_to(hero_direction)


## 날아가는 중인지.
func is_flying() -> bool:
	return _tween != null and _tween.is_valid() and _tween.is_running()


func _on_mouse_button(event: InputEventMouseButton) -> void:
	match event.button_index:
		MOUSE_BUTTON_LEFT:
			if event.pressed:
				_pressed = true
				_dragged = false
				_press_position = event.position
			elif _pressed:
				_pressed = false
				if not _dragged:
					clicked.emit(event.position)
			get_viewport().set_input_as_handled()
		MOUSE_BUTTON_WHEEL_UP:
			if event.pressed:
				zoom_by(-event.factor if event.factor > 0.0 else -1.0)
			get_viewport().set_input_as_handled()
		MOUSE_BUTTON_WHEEL_DOWN:
			if event.pressed:
				zoom_by(event.factor if event.factor > 0.0 else 1.0)
			get_viewport().set_input_as_handled()


func _stop_tween() -> void:
	if _tween != null and _tween.is_valid():
		_tween.kill()
	_tween = null
