extends Node3D
## 지구본 장면 (scenes/globe.tscn). 구운 지구본 자료를 불러와 지구본을 만들고, 키와 자리 읽기를 맡습니다.
##
## 자료는 <굽기 폴더>/globe/ 에서 읽습니다 (기본 res://baked/globe, `-- --baked-dir=<폴더>` 로 바꿈).
## 없으면 굽는 법을, 있는데 잘못됐으면 읽지 못한 까닭을 화면 가운데에 알리고 멈추지 않습니다.
## [br]키는 GlobeHud.HELP_TEXT 에 적혀 있고, 물리 키 위치(physical_keycode)로 직접 받습니다.
## Space(자동 회전)와 F(히어로 유역으로)는 카메라(globe_camera.gd)가 받습니다.

const GlobeCamera := preload("res://scripts/globe_camera.gd")
const CORRIDOR_SCENE := "res://scenes/main.tscn"
## 상태 글을 다시 쓰는 간격 (s).
const STATUS_INTERVAL_S := 0.1
## 고정한 자리 표시 구의 반지름 (지구본 반지름 배).
const PIN_MARKER_RADIUS := 0.006
## 히어로가 없을 때 처음 볼 위도 (도).
const START_LAT_DEG := 20.0

var data: GlobeData
## 클릭으로 고정한 자리 (지구본 좌표의 단위 벡터, 없으면 ZERO).
var pinned_direction := Vector3.ZERO
var _status_timer := 0.0
var _pin_marker: MeshInstance3D

@onready var globe: GlobeView = $Globe
@onready var camera: GlobeCamera = $Camera
@onready var hud: GlobeHud = $Hud


func _ready() -> void:
	hud.corridor_requested.connect(enter_corridor)
	hud.hero_requested.connect(camera.fly_to_hero)
	camera.clicked.connect(_on_clicked)
	data = GlobeData.load_dir()
	if not data.is_valid():
		print("지구본 자료를 읽지 못했습니다: %s" % data.error_message)
		hud.show_missing(data.error_message, data.missing)
		return
	globe.setup(data)
	camera.hero_direction = globe.hero_direction()
	# 처음에는 히어로 유역(없으면 북위 20°, 경도 0°)을 정면에 둡니다.
	var start := globe.hero_direction()
	if start == Vector3.ZERO:
		start = GlobeData.direction_from_lat_lon(START_LAT_DEG, 0.0)
	camera.fly_to(start, 0.0, GlobeCamera.START_DISTANCE)
	globe.display_changed.connect(_place_pin_marker)
	hud.bind(globe)
	_build_pin_marker()
	print("지구본: 면당 %d 칸, 레이어 %d 개, 겹쳐 보기 %d 개, 히어로 %s" % [
		data.face_res, data.fields.size(), data.overlays.size(),
		"있음" if globe.hero_direction() != Vector3.ZERO else "없음"])
	_update_status()


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey) or not event.pressed or event.echo:
		return
	var key: Key = event.physical_keycode
	var handled := true
	match key:
		KEY_M:
			# 장면을 바꾸면 이 노드가 곧바로 트리에서 빠져 get_viewport() 가 null 이 되므로,
			# 입력을 처리했다고 먼저 알리고 바꿉니다.
			get_viewport().set_input_as_handled()
			enter_corridor()
			return
		KEY_H:
			hud.toggle_help()
		KEY_TAB:
			hud.toggle_panel()
		_:
			handled = globe.is_ready() and _globe_key(key, event.shift_pressed)
	if handled:
		get_viewport().set_input_as_handled()


func _process(delta: float) -> void:
	_status_timer -= delta
	if _status_timer <= 0.0:
		_status_timer = STATUS_INTERVAL_S
		_update_status()


## 정렬한 레이어 목록의 index 번째(0부터)를 고릅니다. 숫자 키 1~9 가 0~8 입니다.
func select_field_index(index: int) -> bool:
	var names := data.ordered_field_names()
	if index < 0 or index >= names.size():
		return false
	return globe.set_field(names[index])


## 그 자리의 모든 레이어 값을 왼쪽 위 상자에 고정합니다.
func pin(dir: Vector3) -> void:
	if dir == Vector3.ZERO or not globe.is_ready():
		unpin()
		return
	pinned_direction = dir.normalized()
	hud.set_pinned(hud.describe_all(pinned_direction))
	_place_pin_marker()


func unpin() -> void:
	pinned_direction = Vector3.ZERO
	hud.set_pinned("")
	_place_pin_marker()


## 회랑(걷는 장면)으로 갑니다.
func enter_corridor() -> void:
	get_tree().change_scene_to_file(CORRIDOR_SCENE)


func _globe_key(key: Key, shift: bool) -> bool:
	if key >= KEY_1 and key <= KEY_9:
		select_field_index(int(key - KEY_1))
		return true
	match key:
		KEY_R:
			if shift:
				globe.cycle_river_min_class()
			else:
				globe.toggle_overlay(GlobeView.OVERLAY_RIVERS)
		KEY_K:
			globe.toggle_overlay(GlobeView.OVERLAY_LAKES)
		KEY_L:
			globe.toggle_grid()
		KEY_G:
			globe.toggle_shading()
		KEY_BRACKETLEFT:
			globe.step_exaggeration(-1)
		KEY_BRACKETRIGHT:
			globe.step_exaggeration(1)
		KEY_ESCAPE:
			unpin()
		_:
			return false
	return true


func _on_clicked(screen_position: Vector2) -> void:
	if not globe.is_ready():
		return
	pin(globe.pick(camera, screen_position))


func _build_pin_marker() -> void:
	var sphere := SphereMesh.new()
	sphere.radius = PIN_MARKER_RADIUS
	sphere.height = 2.0 * PIN_MARKER_RADIUS
	sphere.radial_segments = 16
	sphere.rings = 8
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = Color(1.0, 1.0, 1.0)
	_pin_marker = MeshInstance3D.new()
	_pin_marker.name = "PinnedPoint"
	_pin_marker.mesh = sphere
	_pin_marker.material_override = material
	_pin_marker.visible = false
	add_child(_pin_marker)


func _place_pin_marker() -> void:
	if _pin_marker == null:
		return
	_pin_marker.visible = pinned_direction != Vector3.ZERO
	if _pin_marker.visible:
		# 반쯤 땅에 묻히지 않도록 반지름의 절반만큼 띄웁니다.
		var radius := globe.surface_radius(pinned_direction) + 0.5 * PIN_MARKER_RADIUS
		_pin_marker.global_position = globe.to_global(pinned_direction * radius)


func _update_status() -> void:
	if not globe.is_ready():
		return
	var lines: Array[String] = []
	lines.append("B-PCG 지구본 · %s · 고도 과장 ×%s · 자동 회전 %s" % [
		data.label_of(globe.active_field), GlobeHud.format_factor(globe.exaggeration),
		"켜짐" if camera.auto_rotate else "꺼짐"])
	lines.append("카메라: 해수면 위 약 %s km" % GlobeData.format_number(
			(camera.distance - 1.0) * data.radius_m / 1000.0))
	var dir := globe.pick(camera, get_viewport().get_mouse_position())
	if dir == Vector3.ZERO:
		lines.append("마우스가 지구본 밖에 있습니다 · 끌어서 돌리고 휠로 확대합니다")
	else:
		lines.append(hud.describe_point(dir))
	hud.set_status("\n".join(lines))
