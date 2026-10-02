extends SceneTree
## 엔진 연기 검사(smoke test). 화면 없이(--headless) 돌려 핵심 경로가 살아 있는지 봅니다.
##
## 실행 (저장소 맨 위에서, 처음 한 번과 스크립트를 바꾼 뒤에는 --import 를 먼저):[br]
##   godot --headless --path engine --import[br]
##   godot --headless --path engine --script res://tests/smoke.gd[br]
## GDScript 구문 오류나 셰이더 오류가 있어도 Godot 는 종료 코드 0 으로 끝납니다.
## 그래서 성공하면 'BPCG_SMOKE_OK' 를 찍고 quit(0), 실패하면 'BPCG_SMOKE_FAIL: <이유>' 를 찍고
## quit(1) 합니다. tests/test_engine_smoke.py 가 이 표시를 봅니다.
## [br]구운 묶음이 있으면(기본 res://baked, 또는 `-- --baked-dir=<폴더>`) 레이어 불러오기와
## 보이기·숨기기, 단면도 검사합니다. `-- --expect-baked` 를 주면 묶음이 없을 때 실패합니다.
## 묶음에 heightmap_detail 이 있으면 프랙탈 디테일 레이어를 켜고 끄는 것도 검사합니다.

const SAMPLE_STEM := "res://samples/sample"
const MAIN_SCENE := "res://scenes/main.tscn"
const CROSS_SECTION_SHADER := "res://shaders/cross_section.gdshader"
const SCRIPT_DIRS: Array[String] = ["res://scripts"]
## 셰이더 → 꼭 있어야 하는 uniform (컴파일에 실패하면 uniform 목록이 비어 있습니다).
const SHADER_UNIFORMS := {
	"res://shaders/cross_section.gdshader": [
		"plane_normal", "plane_offset_m", "strata_thickness_m", "section_rect", "hole_rect",
		"color_mode", "strata", "palette"],
	"res://shaders/strata_section.gdshader": [
		"strata", "strata_top", "palette", "surface_height", "water_table", "section_rect"],
	"res://shaders/cave.gdshader": ["palette", "use_palette", "plane_normal"],
	"res://shaders/water.gdshader": ["water_color", "plane_normal"],
}
const EXPECT_BAKED_ARG := "--expect-baked"
## 노클립 검사: 이만큼 물리 프레임 동안 키를 누릅니다.
const FLY_FRAMES := 30
const HEIGHT_TOLERANCE_M := 1e-3
## 물리 광선이 맞힌 높이와 표본 높이의 허용 차이 (m).
const RAY_TOLERANCE_M := 0.05
## 플레이어가 땅에 닿기를 기다리는 최대 물리 프레임 수.
const MAX_LANDING_FRAMES := 600
## 검사 전체 시간 제한 (ms). 넘으면 실패로 끝냅니다.
const WATCHDOG_MS := 60000
## 프랙탈 디테일 높이맵 이름 (굽기 폴더 안).
const DETAIL_NAME := "heightmap_detail"

var _done := false
var _started_ms := 0


func _initialize() -> void:
	_started_ms = Time.get_ticks_msec()
	print("Godot %s | 물리 엔진: %s" % [
		Engine.get_version_info().string,
		ProjectSettings.get_setting("physics/3d/physics_engine")])
	_run()


func _process(_delta: float) -> bool:
	if not _done and Time.get_ticks_msec() - _started_ms > WATCHDOG_MS:
		_fail("시간 제한 %d ms 를 넘었습니다" % WATCHDOG_MS)
	return false


func _run() -> void:
	# _initialize() 때는 root 가 아직 트리에 들어가기 전이라 한 프레임 기다립니다.
	await process_frame
	if not _check_scripts():
		return
	if not _check_shader():
		return
	if not _check_heightmap():
		return
	var main := _instantiate_main()
	if main == null:
		return
	if not await _check_physics(main):
		return
	if not await _check_noclip(main):
		return
	if not await _check_fractal_detail(main):
		return
	if not _check_layers(main):
		return
	_pass()


## scripts/ 의 모든 .gd 가 구문 오류 없이 읽히는지 봅니다.
func _check_scripts() -> bool:
	for dir in SCRIPT_DIRS:
		for file in DirAccess.get_files_at(dir):
			if not file.ends_with(".gd"):
				continue
			var path := dir.path_join(file)
			var script := load(path) as GDScript
			if script == null or not script.can_instantiate():
				return _fail("스크립트를 읽지 못했습니다 (구문 오류?): %s" % path)
	return true


## 셰이더가 컴파일되는지 봅니다. 컴파일에 실패하면 uniform 목록이 비어 있습니다.
func _check_shader() -> bool:
	for path in SHADER_UNIFORMS:
		var shader := load(path) as Shader
		if shader == null:
			return _fail("셰이더를 읽지 못했습니다: %s" % path)
		var names := shader.get_shader_uniform_list().map(
				func(u: Dictionary) -> String: return u.name)
		for uniform_name in SHADER_UNIFORMS[path]:
			if not names.has(uniform_name):
				return _fail("셰이더 uniform '%s' 가 없습니다 (컴파일 오류?): %s" % [
					uniform_name, path])
	return true


## 표본 높이맵을 HeightmapLoader 로 읽어 메시와 충돌 모양을 확인합니다.
func _check_heightmap() -> bool:
	var meta: Variant = JSON.parse_string(FileAccess.get_file_as_string(SAMPLE_STEM + ".json"))
	if not (meta is Dictionary):
		return _fail("표본 설명 파일을 읽지 못했습니다: %s.json" % SAMPLE_STEM)
	var hm := HeightmapLoader.load_stem(SAMPLE_STEM)
	if not hm.is_valid():
		return _fail("HeightmapLoader 가 표본을 읽지 못했습니다: " + hm.error_message)
	var w := int(meta["width"])
	var h := int(meta["height"])
	if hm.width != w or hm.height != h or hm.heights.size() != w * h:
		return _fail("높이맵 크기가 다릅니다: %d x %d, 표본 %d개" % [
			hm.width, hm.height, hm.heights.size()])

	var mesh := hm.build_mesh()
	var arrays := mesh.surface_get_arrays(0)
	var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
	var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
	var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
	if vertices.size() != w * h:
		return _fail("꼭짓점 수 %d 가 width * height = %d 와 다릅니다" % [vertices.size(), w * h])
	if normals.size() != w * h:
		return _fail("법선 수 %d 가 꼭짓점 수와 다릅니다" % normals.size())
	if indices.size() != (w - 1) * (h - 1) * 6:
		return _fail("인덱스 수 %d 가 맞지 않습니다" % indices.size())

	var lo := INF
	var hi := -INF
	for v in vertices:
		lo = minf(lo, v.y)
		hi = maxf(hi, v.y)
	if (absf(lo - float(meta["min"])) > HEIGHT_TOLERANCE_M
			or absf(hi - float(meta["max"])) > HEIGHT_TOLERANCE_M):
		return _fail("메시 높이 범위 %f ~ %f 가 json 의 %s ~ %s 와 다릅니다" % [
			lo, hi, meta["min"], meta["max"]])
	for n in normals:
		if n.y <= 0.0:
			return _fail("아래를 향한 법선이 있습니다: %s" % n)
	# Plane(a, b, c) 는 시계 방향 점 순서로 법선을 정합니다. 앞면이 위를 봐야 합니다.
	var first := Plane(vertices[indices[0]], vertices[indices[1]], vertices[indices[2]])
	if first.normal.y <= 0.0:
		return _fail("삼각형 감김 방향이 반대입니다 (앞면이 아래를 봄)")

	var shape := hm.build_shape()
	if shape.map_width != w or shape.map_depth != h or shape.map_data.size() != w * h:
		return _fail("충돌 모양 크기가 다릅니다: %d x %d" % [shape.map_width, shape.map_depth])
	if (absf(shape.get_min_height() - float(meta["min"])) > HEIGHT_TOLERANCE_M
			or absf(shape.get_max_height() - float(meta["max"])) > HEIGHT_TOLERANCE_M):
		return _fail("충돌 모양 높이 범위 %f ~ %f 가 json 과 다릅니다" % [
			shape.get_min_height(), shape.get_max_height()])

	var row := floori((h - 1) / 3.0)
	var col := floori((w - 1) / 4.0)
	var probe := hm.origin + Vector3(col * hm.spacing_m, 0.0, row * hm.spacing_m)
	var expected := hm.origin.y + hm.heights[row * w + col]
	if absf(hm.height_at(probe) - expected) > HEIGHT_TOLERANCE_M:
		return _fail("height_at 이 표본 값과 다릅니다: %f, 기대 %f" % [hm.height_at(probe), expected])
	print("높이맵: %d x %d, 간격 %.1f m, 높이 %.2f ~ %.2f m" % [w, h, hm.spacing_m, lo, hi])
	return true


## 시작 장면을 만들어 트리에 붙이고 구성이 맞는지 봅니다. 실패하면 null.
func _instantiate_main() -> Node:
	var packed := load(MAIN_SCENE) as PackedScene
	if packed == null:
		_fail("시작 장면을 읽지 못했습니다: %s" % MAIN_SCENE)
		return null
	var main := packed.instantiate()
	if main == null:
		_fail("시작 장면을 만들지 못했습니다: %s" % MAIN_SCENE)
		return null
	root.add_child(main)

	var terrain := main.get_node_or_null("Terrain") as HeightmapTerrain
	if terrain == null or terrain.heightmap == null or not terrain.heightmap.is_valid():
		_fail("Terrain 노드가 없거나 높이맵을 읽지 못했습니다")
		return null
	if not (terrain.get_node_or_null("Mesh") is MeshInstance3D):
		_fail("Terrain 아래 Mesh 가 없습니다")
		return null
	var collision := terrain.get_node_or_null("Body/Shape") as CollisionShape3D
	if collision == null or not (collision.shape is HeightMapShape3D):
		_fail("Terrain 아래 Body/Shape (HeightMapShape3D) 가 없습니다")
		return null
	var material := terrain.material as ShaderMaterial
	if (material == null or material.shader == null
			or material.shader.resource_path != CROSS_SECTION_SHADER):
		_fail("Terrain 재질이 단면 셰이더가 아닙니다")
		return null

	var camera := main.get_node_or_null("Player/Head/Camera") as Camera3D
	if camera == null or camera.far < 10000.0:
		_fail("플레이어 카메라가 없거나 far 가 10000 m 보다 짧습니다")
		return null
	var env_node := main.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if env_node == null or env_node.environment == null or env_node.environment.sky == null:
		_fail("WorldEnvironment 나 하늘(Sky)이 없습니다")
		return null
	if not env_node.environment.fog_enabled:
		_fail("안개가 꺼져 있습니다")
		return null
	if not (main.get_node_or_null("Sun") is DirectionalLight3D):
		_fail("DirectionalLight3D (Sun) 가 없습니다")
		return null
	if not (main.get_node_or_null("Baked") is BakedLayers):
		_fail("Baked (BakedLayers) 노드가 없습니다")
		return null
	if not (main.get_node_or_null("Hud") is Hud):
		_fail("Hud 노드가 없습니다")
		return null
	if not ResourceLoader.exists(main.GLOBE_SCENE):
		_fail("M 키로 가는 지구본 장면이 없습니다: %s" % main.GLOBE_SCENE)
		return null
	if not (main.get_node_or_null("Player/Head/Camera/Lamp") is Light3D):
		_fail("손전등 (Player/Head/Camera/Lamp) 이 없습니다")
		return null
	print("시작 장면: 지형 %s" % terrain.used_stem)
	return main


## 물리 광선이 높이맵과 같은 자리를 맞히는지, 플레이어가 땅에 내려서는지 봅니다.
func _check_physics(main: Node) -> bool:
	var terrain := main.get_node("Terrain") as HeightmapTerrain
	var player := main.get_node("Player") as Player
	var hm := terrain.heightmap
	# 이 검사는 걷기에서 합니다. 시작은 노클립입니다.
	if not player.noclip:
		return _fail("플레이어가 노클립으로 시작하지 않았습니다")
	player.set_noclip(false)
	await physics_frame
	await physics_frame
	# 주변 25 m 지형의 충돌면은 회랑 경계 안쪽 한 칸까지 겹치므로 광선에서 뺍니다.
	var exclude: Array[RID] = [player.get_rid()]
	var surround_body := main.get_node_or_null("Baked/Surround/Body") as StaticBody3D
	if surround_body != null:
		exclude.append(surround_body.get_rid())

	var space := terrain.get_world_3d().direct_space_state
	var worst := 0.0
	var last_row := hm.height - 1
	var last_col := hm.width - 1
	var extent := Vector3(last_col * hm.spacing_m, 0.0, last_row * hm.spacing_m)
	var samples: Array[Vector2i] = [
		Vector2i(0, 0),
		Vector2i(floori(last_row / 3.0), floori(last_col / 4.0)),
		Vector2i(floori(last_row / 2.0), floori(last_col / 2.0)),
		Vector2i(last_row, last_col),
	]
	for rc in samples:
		var local := hm.origin + Vector3(rc.y * hm.spacing_m, 0.0, rc.x * hm.spacing_m)
		# 가장자리 표본은 모양 경계에 걸리므로 안쪽으로 1 cm 옮겨 쏩니다.
		local.x = clampf(local.x, hm.origin.x + 0.01, hm.origin.x + extent.x - 0.01)
		local.z = clampf(local.z, hm.origin.z + 0.01, hm.origin.z + extent.z - 0.01)
		var top := terrain.to_global(
				Vector3(local.x, hm.origin.y + hm.max_height_m + 100.0, local.z))
		var bottom := terrain.to_global(
				Vector3(local.x, hm.origin.y + hm.min_height_m - 100.0, local.z))
		# 플레이어가 지형 가운데 서 있으므로 광선에서 뺍니다.
		var query := PhysicsRayQueryParameters3D.create(top, bottom, 0xFFFFFFFF, exclude)
		var hit := space.intersect_ray(query)
		if hit.is_empty():
			return _fail("물리 광선이 지형을 맞히지 못했습니다: 행 %d, 열 %d" % [rc.x, rc.y])
		var expected := terrain.to_global(Vector3(local.x, terrain.height_at(local), local.z)).y
		var err := absf(hit.position.y - expected)
		worst = maxf(worst, err)
		if err > RAY_TOLERANCE_M:
			return _fail("물리 광선 높이 %f 가 높이맵 %f 와 %f m 다릅니다 (행 %d, 열 %d)" % [
				hit.position.y, expected, err, rc.x, rc.y])

	var frames := 0
	while not player.is_on_floor() and frames < MAX_LANDING_FRAMES:
		await physics_frame
		frames += 1
	if not player.is_on_floor():
		return _fail("플레이어가 %d 물리 프레임 안에 땅에 닿지 않았습니다 (y = %f)" % [
			MAX_LANDING_FRAMES, player.global_position.y])
	var feet := terrain.to_local(player.global_position)
	var ground := terrain.to_global(Vector3(feet.x, terrain.height_at(feet), feet.z)).y
	if absf(player.global_position.y - ground) > 0.2:
		return _fail("플레이어 발 높이 %f 가 지면 %f 와 다릅니다" % [player.global_position.y, ground])
	print("물리: 광선 오차 최대 %.4f m, 플레이어 착지 %d 프레임" % [worst, frames])
	return true


## 노클립: 충돌을 끄고, 중력 없이 바라보는 방향과 위로 납니다.
func _check_noclip(main: Node) -> bool:
	var player := main.get_node("Player") as Player
	player.set_noclip(true)
	if not player.shape.disabled:
		return _fail("노클립인데 충돌 모양이 켜져 있습니다")
	player.head.rotation.x = 0.0
	await physics_frame
	var start := player.global_position
	var forward := -player.global_basis.z
	Input.action_press("move_forward")
	for i in FLY_FRAMES:
		await physics_frame
	Input.action_release("move_forward")
	var moved := player.global_position - start
	var expected := player.fly_speed_m_s * FLY_FRAMES / float(Engine.physics_ticks_per_second)
	if absf(moved.dot(forward) - expected) > 0.25 * expected or absf(moved.y) > 1e-3:
		return _fail("노클립 앞으로 날기: 움직임 %s, 기대 앞으로 %.2f m, 높이 변화 0" % [
			moved, expected])
	start = player.global_position
	Input.action_press("fly_up")
	for i in FLY_FRAMES:
		await physics_frame
	Input.action_release("fly_up")
	var rise := player.global_position.y - start.y
	if absf(rise - expected) > 0.25 * expected:
		return _fail("노클립 위로 날기: %.2f m 올랐습니다 (기대 %.2f m)" % [rise, expected])
	# 땅속에서 걷기로 바꾸면 지면 위로 올라옵니다.
	var terrain := main.get_node("Terrain") as HeightmapTerrain
	var c := terrain.heightmap.center()
	player.global_position = terrain.to_global(c - Vector3(0.0, 30.0, 0.0))
	player.set_noclip(false)
	var ground := terrain.to_global(c).y
	if player.global_position.y < ground:
		return _fail("땅속에서 걷기로 바꿨는데 지면 아래에 있습니다: %.2f < %.2f" % [
			player.global_position.y, ground])
	player.set_noclip(true)
	print("노클립: 앞 %.2f m, 위 %.2f m (%d 프레임)" % [moved.dot(forward), rise, FLY_FRAMES])
	return true


## 구운 레이어: 있으면 각 레이어의 보이기·숨기기, 만 보기, 단면을 검사합니다.
func _check_layers(main: Node) -> bool:
	var baked := main.get_node("Baked") as BakedLayers
	var hud := main.get_node("Hud") as Hud
	var terrain := main.get_node("Terrain") as HeightmapTerrain
	if not baked.loaded:
		if OS.get_cmdline_user_args().has(EXPECT_BAKED_ARG):
			return _fail("구운 묶음을 기대했지만 불러오지 못했습니다: %s" % BakedPaths.dir())
		print("레이어: 구운 묶음이 없어 표본 지형만 검사했습니다")
		return true
	var ids := baked.layer_ids()
	for need in ["terrain", "surround", "water", "water_table", "section"]:
		if not ids.has(need):
			return _fail("레이어 '%s' 가 없습니다 (있는 것: %s)" % [need, ids])
	var man := baked.manifest
	var meta: Dictionary = man["file_meta"]
	if man["files"].has("caves.glb"):
		if not ids.has("caves"):
			return _fail("caves.glb 가 있는데 동굴 레이어가 없습니다")
		if baked.cave_faces != int(meta["caves"]["faces"]):
			return _fail("동굴 삼각형 %d 개가 manifest 의 %d 와 다릅니다" % [
				baked.cave_faces, int(meta["caves"]["faces"])])
	var water := baked.get_node("Water") as MeshInstance3D
	var n_wet := int(meta["water"].get("n_wet", 0))
	if (n_wet > 0) != (water.mesh.get_surface_count() > 0):
		return _fail("물 표본 %d 개인데 수면 메시 면 수가 %d 입니다" % [
			n_wet, water.mesh.get_surface_count()])
	if baked.entrances_inside.size() != int(man["caves"].get("n_entrances", 0)):
		return _fail("동굴 입구 %d 곳이 manifest 의 %d 와 다릅니다" % [
			baked.entrances_inside.size(), int(man["caves"].get("n_entrances", 0))])
	var st: Dictionary = meta["strata"]
	if baked.strata == null or [baked.strata.rows, baked.strata.layers, baked.strata.cols] \
			!= [int(st["shape"][0]), int(st["shape"][1]), int(st["shape"][2])]:
		return _fail("재질 부피를 읽지 못했거나 크기가 다릅니다")
	# 지표 10 m 아래 표본은 재질 부피의 값이어야 합니다 (255 공기, 254 물 포함).
	var c := terrain.heightmap.center()
	var probe := terrain.to_global(c - Vector3(0.0, 10.0, 0.0))
	var rock := baked.strata.id_at(probe)
	if rock < 0 or rock > StrataVolume.AIR_ID:
		return _fail("지표 10 m 아래 재질 번호가 %d 입니다" % rock)

	# 보이기·숨기기: 레이어마다 끄고 켭니다. 지형 버튼도 같이 바뀌어야 합니다.
	for id in ids:
		var before := baked.is_layer_visible(id)
		baked.toggle_layer(id)
		if baked.is_layer_visible(id) == before:
			return _fail("레이어 '%s' 가 바뀌지 않았습니다" % id)
		var button := hud.layer_button(id)
		if button == null or button.button_pressed != baked.is_layer_visible(id):
			return _fail("레이어 '%s' 버튼 상태가 레이어와 다릅니다" % id)
		baked.toggle_layer(id)
	if not terrain.is_mesh_visible():
		return _fail("지형을 끄고 켰는데 보이지 않습니다")
	baked.solo("caves" if ids.has("caves") else "water")
	for id in ids:
		if baked.can_solo(id):
			var want := id == ("caves" if ids.has("caves") else "water")
			if baked.is_layer_visible(id) != want:
				return _fail("만 보기 뒤 '%s' 보이기가 %s 입니다" % [id, baked.is_layer_visible(id)])
	baked.show_all()
	if not (terrain.is_mesh_visible() and baked.is_layer_visible("surround")):
		return _fail("모두 보기 뒤에 지형이 숨어 있습니다")

	# 단면: 켜면 판이 보이고 지형 재질의 자르는 면이 바뀝니다.
	var m := terrain.material as ShaderMaterial
	baked.set_section(true, terrain.to_global(c), Vector3.BACK)
	var section := baked.get_node("Section") as MeshInstance3D
	var offset: float = m.get_shader_parameter("plane_offset_m")
	if not section.visible or absf(offset - Vector3.BACK.dot(terrain.to_global(c))) > 1e-3:
		return _fail("단면을 켰는데 판이 안 보이거나 자르는 면이 다릅니다 (%f)" % offset)
	baked.move_section(5.0)
	offset = m.get_shader_parameter("plane_offset_m")
	if absf(offset - (Vector3.BACK.dot(terrain.to_global(c)) - 5.0)) > 1e-3:
		return _fail("단면을 5 m 밀었는데 자르는 면이 %f 입니다" % offset)
	baked.set_section(false)
	if section.visible or float(m.get_shader_parameter("plane_offset_m")) < 1.0e8:
		return _fail("단면을 껐는데 판이 보이거나 자르는 면이 남아 있습니다")
	print("레이어: %s, 동굴 삼각형 %d (%s), 입구 %d, 재질 부피 %d × %d × %d" % [
		", ".join(ids), baked.cave_faces, baked.cave_source, baked.entrances_inside.size(),
		baked.strata.cols, baked.strata.layers, baked.strata.rows])
	return true


## 프랙탈 디테일: heightmap_detail 이 있으면 레이어가 맨 끝에 있고 처음에 켜져 있으며, 숫자 키와
## 패널 버튼으로 끄고 켤 때 회랑 지형(높이, 충돌)·단면 판의 지표·입구 구멍·버튼이 같이 바뀌는지,
## 지형을 숨긴 채 바꿔도 숨은 채인지, 걷는 플레이어가 새 땅속에 묻히지 않는지 봅니다.
## 파일이 없으면 레이어도 없어야 합니다.
func _check_fractal_detail(main: Node) -> bool:
	var baked := main.get_node("Baked") as BakedLayers
	if not baked.loaded:
		return true
	var hud := main.get_node("Hud") as Hud
	var terrain := main.get_node("Terrain") as HeightmapTerrain
	var player := main.get_node("Player") as Player
	var ids := baked.layer_ids()
	var has_file := HeightmapLoader.exists(BakedPaths.dir().path_join(DETAIL_NAME))
	if ids.has("fractal_detail") != has_file:
		return _fail("heightmap_detail 파일 있음 = %s 인데 프랙탈 디테일 레이어 있음 = %s" % [
			has_file, ids.has("fractal_detail")])
	if not has_file:
		print("프랙탈 디테일: 굽기 파일이 없어 레이어도 없습니다")
		return true
	if ids.back() != "fractal_detail" or baked.can_solo("fractal_detail"):
		return _fail("프랙탈 디테일이 맨 끝 선택 레이어가 아닙니다: %s" % [ids])
	if not baked.is_layer_visible("fractal_detail"):
		return _fail("프랙탈 디테일 파일이 있는데 처음에 꺼져 있습니다")
	if not terrain.used_stem.ends_with(DETAIL_NAME):
		return _fail("프랙탈 디테일이 켜졌는데 지형이 %s 입니다" % terrain.used_stem)
	var button := hud.layer_button("fractal_detail")
	if button == null or not button.button_pressed:
		return _fail("프랙탈 디테일 버튼이 없거나 눌려 있지 않습니다")
	var detail := terrain.heightmap
	var base := HeightmapLoader.load_stem(BakedPaths.dir().path_join("heightmap"))
	if not base.is_valid() or base.heights.size() != detail.heights.size():
		return _fail("기본 높이맵을 읽지 못했거나 디테일과 격자가 다릅니다")
	# 두 높이맵이 가장 많이 다른 안쪽 표본에서 높이와 충돌면이 바뀌는지 봅니다.
	var worst := Vector2i.ZERO
	var worst_m := 0.0
	for row in range(1, detail.height - 1):
		for col in range(1, detail.width - 1):
			var i := row * detail.width + col
			var d := absf(detail.heights[i] - base.heights[i])
			if d > worst_m:
				worst = Vector2i(col, row)
				worst_m = d
	if worst_m < 1e-3:
		return _fail("heightmap_detail 이 heightmap 과 같습니다")
	var probe := detail.origin + Vector3(worst.x, 0.0, worst.y) * detail.spacing_m
	var section := baked.get_node_or_null("Section") as MeshInstance3D
	var section_m := section.material_override as ShaderMaterial if section != null else null
	var detail_surface: Variant = (
			section_m.get_shader_parameter("surface_height") if section_m != null else null)
	var m := terrain.material as ShaderMaterial
	var detail_mouth: Variant = m.get_shader_parameter("cave_mouth")
	var has_mouth_detail := HeightmapLoader.exists(BakedPaths.dir().path_join("cave_mouth_detail"))

	# 끄기: 숫자 키 (패널 순서 번호)
	var key := KEY_1 + ids.find("fractal_detail")
	var t := Time.get_ticks_msec()
	main._unhandled_input(_key_event(key))
	var off_ms := Time.get_ticks_msec() - t
	if baked.is_layer_visible("fractal_detail") or button.button_pressed:
		return _fail("%s 키로 프랙탈 디테일이 꺼지지 않았거나 버튼이 눌려 있습니다" % OS.get_keycode_string(key))
	if terrain.used_stem != BakedPaths.dir().path_join("heightmap"):
		return _fail("프랙탈 디테일을 껐는데 지형이 %s 입니다" % terrain.used_stem)
	if not terrain.is_mesh_visible():
		return _fail("프랙탈 디테일을 끄니 지형 메시가 숨었습니다")
	if not await _expect_ground(terrain, main, probe, base, "끈 뒤"):
		return false
	if section_m != null and section_m.get_shader_parameter("surface_height") == detail_surface:
		return _fail("프랙탈 디테일을 껐는데 단면 판의 지표 높이 텍스처가 그대로입니다")
	if has_mouth_detail and m.get_shader_parameter("cave_mouth") == detail_mouth:
		return _fail("프랙탈 디테일을 껐는데 동굴 입구 텍스처가 그대로입니다")

	# 켜기: 패널 버튼. 지형을 숨긴 채 바꾸면 숨은 채여야 합니다. 단면을 켠 채 바꾸면 단면 판이
	# 새 지표의 가장 높은 곳까지 덮어야 합니다.
	baked.set_layer_visible("terrain", false)
	baked.set_section(true, terrain.to_global(probe), Vector3.BACK)
	t = Time.get_ticks_msec()
	button.button_pressed = true
	var on_ms := Time.get_ticks_msec() - t
	if section != null:
		var top_y := section.global_position.y + 0.5 * (section.mesh as QuadMesh).size.y
		var max_y := terrain.to_global(Vector3(0.0, detail.origin.y + detail.max_height_m, 0.0)).y
		if top_y < max_y:
			return _fail("프랙탈 디테일을 켰는데 단면 판 위 끝 %.2f 가 지표 최고 %.2f 보다 낮습니다" % [
				top_y, max_y])
	baked.set_section(false)
	if not baked.is_layer_visible("fractal_detail") or not terrain.used_stem.ends_with(DETAIL_NAME):
		return _fail("패널 버튼으로 프랙탈 디테일이 켜지지 않았습니다 (지형 %s)" % terrain.used_stem)
	if terrain.is_mesh_visible() or baked.is_layer_visible("terrain"):
		return _fail("지형을 숨긴 채 프랙탈 디테일을 켰는데 지형 메시가 보입니다")
	baked.set_layer_visible("terrain", true)
	if terrain.heightmap != detail:
		return _fail("프랙탈 디테일을 다시 켰는데 전에 만든 높이맵을 다시 쓰지 않았습니다")
	if not await _expect_ground(terrain, main, probe, detail, "다시 켠 뒤"):
		return false
	if section_m != null and section_m.get_shader_parameter("surface_height") != detail_surface:
		return _fail("프랙탈 디테일을 다시 켰는데 단면 판의 지표 높이 텍스처가 돌아오지 않았습니다")
	if m.get_shader_parameter("cave_mouth") != detail_mouth:
		return _fail("프랙탈 디테일을 다시 켰는데 동굴 입구 텍스처가 돌아오지 않았습니다")

	# 걷는 중에 바꾸면 새 지면 위로 올라와야 합니다.
	player.set_noclip(false)
	for _i in 2:
		var feet := terrain.to_global(Vector3(probe.x, terrain.height_at(probe) - 3.0, probe.z))
		player.global_position = feet
		baked.toggle_layer("fractal_detail")
		var ground: float = main._ground_y(player.global_position)
		if not (player.global_position.y >= ground):
			player.set_noclip(true)
			return _fail("걷는 중에 프랙탈 디테일을 바꿨는데 땅속에 있습니다: %.2f < %.2f" % [
				player.global_position.y, ground])
	player.set_noclip(true)
	if not baked.is_layer_visible("fractal_detail"):
		return _fail("프랙탈 디테일을 두 번 바꿨는데 켜져 있지 않습니다")
	print("프랙탈 디테일: 켜짐 (처음), %s 키, 높이 차 최대 %.2f m, 끄기 %d ms, 다시 켜기 %d ms" % [
		OS.get_keycode_string(key), worst_m, off_ms, on_ms])
	return true


## 지형의 높이(height_at)와 충돌면(물리 광선)이 local 에서 높이맵 hm 의 값과 같은지 봅니다.
func _expect_ground(
		terrain: HeightmapTerrain, main: Node, local: Vector3, hm: HeightmapLoader,
		when: String) -> bool:
	var want := hm.height_at(local)
	if absf(terrain.height_at(local) - want) > HEIGHT_TOLERANCE_M:
		return _fail("프랙탈 디테일을 %s 지형 높이 %f 가 기대 %f 와 다릅니다" % [
			when, terrain.height_at(local), want])
	# 새 충돌체가 물리 공간에 들어가도록 기다립니다.
	await physics_frame
	await physics_frame
	var exclude: Array[RID] = [(main.get_node("Player") as Player).get_rid()]
	var surround_body := main.get_node_or_null("Baked/Surround/Body") as StaticBody3D
	if surround_body != null:
		exclude.append(surround_body.get_rid())
	var top := terrain.to_global(Vector3(local.x, hm.origin.y + hm.max_height_m + 100.0, local.z))
	var bottom := terrain.to_global(
			Vector3(local.x, hm.origin.y + hm.min_height_m - 100.0, local.z))
	var query := PhysicsRayQueryParameters3D.create(top, bottom, 0xFFFFFFFF, exclude)
	var hit := terrain.get_world_3d().direct_space_state.intersect_ray(query)
	var expected := terrain.to_global(Vector3(local.x, want, local.z)).y
	if hit.is_empty() or absf(hit.position.y - expected) > RAY_TOLERANCE_M:
		return _fail("프랙탈 디테일을 %s 물리 광선이 %s, 기대 높이 %f" % [
			when, "빗나감" if hit.is_empty() else str(hit.position.y), expected])
	return true


func _key_event(key: Key) -> InputEventKey:
	var event := InputEventKey.new()
	event.physical_keycode = key
	event.keycode = key
	event.pressed = true
	return event


func _pass() -> void:
	if _done:
		return
	_done = true
	print("BPCG_SMOKE_OK")
	quit(0)


func _fail(reason: String) -> bool:
	if not _done:
		_done = true
		print("BPCG_SMOKE_FAIL: " + reason)
		quit(1)
	return false
