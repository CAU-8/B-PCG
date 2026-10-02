extends Node3D
## 시작 장면. 지형과 구운 레이어를 불러오고, 플레이어를 세우고, 보기 전환 키를 받습니다.
##
## 자식의 _ready() 가 부모보다 먼저 불리므로 여기서는 지형이 이미 준비돼 있습니다.
## 키는 Hud.HELP_TEXT 에 적혀 있습니다. 레이어 숫자 키(1~9, Shift, 0)는 여기서 직접 받습니다.
## 숫자는 패널 순서(BakedLayers.layer_ids)를 따릅니다. 구운 레이어가 다 있으면 1~8 입니다.

## 단면을 켤 때 눈앞 거리 (m).
const SECTION_DISTANCE_M := 15.0
## [ ] 키로 단면을 옮기는 속력 (m/s, Shift 면 5 배).
const SECTION_SPEED_M_S := 10.0
## 노클립으로 시작할 때 지면 위 높이 (m).
const NOCLIP_SPAWN_CLEARANCE_M := 40.0
## 동굴 입구로 옮길 때 산비탈 밖 끝에서 더 물러나는 거리와 높이 (m).
const ENTRANCE_BACKOFF_M := 4.0
const ENTRANCE_EYE_UP_M := 1.0
## 상태 글을 다시 쓰는 간격 (s).
const STATUS_INTERVAL_S := 0.1
const COLOR_MODE_NAMES := {1: "자연색", 2: "지질도 (흙 아래 기반암)"}
## M 키로 가는 지구본 장면 (행성 전체, engine/GLOBE.md).
const GLOBE_SCENE := "res://scenes/globe.tscn"

var _color_mode := 1
var _entrance_index := -1
var _status_timer := 0.0

@onready var terrain: HeightmapTerrain = $Terrain
@onready var baked: BakedLayers = $Baked
@onready var player: Player = $Player
@onready var hud: Hud = $Hud
@onready var lamp: Light3D = $Player/Head/Camera/Lamp


func _ready() -> void:
	baked.section_anchor = _section_in_front
	baked.setup(terrain)
	player.ground_height_at = _ground_y
	var clearance := NOCLIP_SPAWN_CLEARANCE_M if player.noclip else 2.0
	player.global_position = terrain.spawn_point(clearance)
	hud.bind_layers(baked)
	hud.action_requested.connect(_on_hud_action)
	baked.terrain_rebuilt.connect(_on_terrain_rebuilt)
	_update_option_labels()
	_update_status()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		var key: Key = event.physical_keycode
		if key >= KEY_1 and key <= KEY_9:
			var ids := baked.layer_ids()
			var i := int(key - KEY_1)
			if i < ids.size():
				if event.shift_pressed and baked.can_solo(ids[i]):
					baked.solo(ids[i])
				else:
					baked.toggle_layer(ids[i])
			return
		if key == KEY_0:
			baked.show_all()
			return
		if key == KEY_M:
			open_globe()
			return
	if event.is_action_pressed("toggle_section"):
		baked.toggle_layer("section")
	elif event.is_action_pressed("toggle_color_mode"):
		cycle_color_mode()
	elif event.is_action_pressed("toggle_lamp"):
		lamp.visible = not lamp.visible
		_update_option_labels()
	elif event.is_action_pressed("next_entrance"):
		go_to_entrance(-1 if event.shift_pressed else 1)
	elif event.is_action_pressed("toggle_help"):
		hud.toggle_help()
	elif event.is_action_pressed("toggle_panel"):
		hud.toggle_panel()


func _process(delta: float) -> void:
	if baked.section_on:
		var push := Input.get_axis("section_pull", "section_push")
		if push != 0.0:
			var fast := 5.0 if Input.is_action_pressed("move_fast") else 1.0
			baked.move_section(push * SECTION_SPEED_M_S * fast * delta)
	_status_timer -= delta
	if _status_timer <= 0.0:
		_status_timer = STATUS_INTERVAL_S
		_update_status()


## 지구본 장면(행성 전체)으로 갑니다. 지구본에서 M 을 누르면 이 장면으로 돌아옵니다.
func open_globe() -> void:
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	get_tree().change_scene_to_file(GLOBE_SCENE)


## 지형 색: 자연색 ↔ 지질도. 구운 재질 부피가 없으면 바꾸지 않습니다.
func cycle_color_mode() -> void:
	var m := terrain.material as ShaderMaterial
	if m == null or baked.strata == null:
		return
	_color_mode = 2 if _color_mode == 1 else 1
	m.set_shader_parameter("color_mode", _color_mode)
	_update_option_labels()


## step 번째 다음 동굴 입구 앞으로 옮겨 입구 안쪽을 봅니다 (노클립으로 바꿈).
func go_to_entrance(step: int) -> void:
	var n := baked.entrances_inside.size()
	if n == 0:
		return
	_entrance_index = posmod(_entrance_index + step, n)
	var inside := baked.entrances_inside[_entrance_index]
	var outside := baked.entrances_outside[_entrance_index]
	var out_dir := outside - inside
	out_dir.y = 0.0
	out_dir = out_dir.normalized() if out_dir.length() > 1e-6 else Vector3.BACK
	var eye := outside + out_dir * ENTRANCE_BACKOFF_M + Vector3.UP * ENTRANCE_EYE_UP_M
	if not player.noclip:
		player.set_noclip(true)
	player.look_from(eye, inside)


func _on_hud_action(action: String) -> void:
	match action:
		"color_mode":
			cycle_color_mode()
		"globe":
			open_globe()
		"lamp":
			lamp.visible = not lamp.visible
			_update_option_labels()


## 회랑 지형 높이맵이 바뀌면 (프랙탈 디테일) 걷는 플레이어가 새 땅속에 묻히지 않게 올립니다.
func _on_terrain_rebuilt() -> void:
	if not player.noclip:
		player.lift_above_ground()


func _update_option_labels() -> void:
	var color_label: String = COLOR_MODE_NAMES.get(_color_mode, "높이 무늬")
	if baked.strata == null:
		color_label = "자연색" if baked.loaded else "높이 무늬 (표본)"
	hud.set_option_labels(color_label, lamp.visible)


## 눈앞 SECTION_DISTANCE_M 에 수직으로 선 자르는 면 [점, 법선(카메라 쪽)].
func _section_in_front() -> Array:
	var eye := player.eye_position()
	var forward := -player.head.global_basis.z
	forward.y = 0.0
	forward = forward.normalized() if forward.length() > 1e-6 else Vector3.FORWARD
	return [eye + forward * SECTION_DISTANCE_M, -forward]


## 전역 위치의 회랑 지면 높이 (엔진 Y). 회랑 밖이면 주변 지형 높이, 그것도 없으면 NAN.
func _ground_y(p: Vector3) -> float:
	var h := terrain.height_at(terrain.to_local(p))
	if is_finite(h):
		return terrain.to_global(Vector3(0.0, h, 0.0)).y
	var surround := baked.get_node_or_null("Surround") as HeightmapTerrain
	if surround != null:
		var hs := surround.height_at(surround.to_local(p))
		if is_finite(hs):
			return surround.to_global(Vector3(0.0, hs, 0.0)).y
	return NAN


func _update_status() -> void:
	var eye := player.eye_position()
	var lines: Array[String] = []
	var mode := "날기 (노클립)" if player.noclip else "걷기"
	lines.append("%s · 속력 %.0f m/s" % [mode, player.current_speed_m_s()])
	var where := "동 %+.0f m, 북 %+.0f m (회랑 가운데 기준)" % [eye.x, -eye.z]
	if baked.loaded:
		where += " · 해발 %.0f m" % (eye.y + baked.y_offset_m)
	lines.append(where)
	var ground := _ground_y(eye)
	if is_finite(ground):
		if eye.y < ground:
			var info := "땅속 %.0f m" % (ground - eye.y)
			if baked.strata != null:
				var id := baked.strata.id_at(eye)
				if id >= 0:
					info += " · %s" % baked.strata.name_of(id)
				elif ground - eye.y > baked.strata.depth_m():
					info += " · 구운 깊이(%.0f m)보다 깊음" % baked.strata.depth_m()
			lines.append(info)
		else:
			lines.append("지표 위 %.0f m" % (eye.y - ground))
		var wt := baked.water_table_y(eye)
		if is_finite(wt):
			lines.append("이 자리 지하수면: 지표 아래 %.1f m" % (ground - wt))
	if baked.section_on:
		lines.append("단면 켜짐 · 눈에서 %.0f m ([ ] 로 옮김, X 로 끔)" % absf(
				baked.section_normal.dot(eye - baked.section_point)))
	if baked.entrances_inside.size() > 0:
		var at := "" if _entrance_index < 0 else " (지금 %d 번)" % (_entrance_index + 1)
		lines.append("동굴 입구 %d 곳%s · T 로 옮겨 가기" % [baked.entrances_inside.size(), at])
	hud.set_status("\n".join(lines))
