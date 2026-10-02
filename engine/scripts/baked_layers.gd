class_name BakedLayers
extends Node3D
## Python 이 구운 회랑 묶음(manifest.json)의 레이어를 불러오고, 보이기·숨기기를 맡습니다.
##
## 회랑 지표 높이맵은 Terrain 노드가 읽고, 여기서는 나머지를 자식으로 만듭니다.[br]
## - Surround: 히어로 전체 25 m 지형 (surround25). 회랑 자리는 비워 고운 지형에 맡깁니다.[br]
## - Water: 호수·강 수면 (water).[br]
## - WaterTable: 지하수면 (water_table). 처음에는 숨기고, 처음 켤 때 메시를 만듭니다.[br]
## - Caves: 동굴 메시 (caves.glb).[br]
## - Section: 단면 판. 재질 부피(strata.u8)를 3D 텍스처로 읽어 자르는 면에 칠합니다.[br]
## 레이어 이름은 LAYERS 의 키입니다. "entrance_holes" 는 지형에 동굴 입구 구멍을 뚫는 선택입니다
## (cave_mouth: 지표 점의 동굴 거리, 음수인 곳을 뚫음).[br]
## "fractal_detail" 은 회랑 지형을 프랙탈 디테일 높이맵(heightmap_detail)으로 바꾸는 선택입니다.
## 굽기가 히어로 격자로 그리지 못하는 100 m 보다 짧은 파장의 거칠기를 이어 붙인 보기용 지표이고,
## 솔버 결과가 아닙니다. 파일이 있으면 처음에 켭니다. 끄면 기본 높이맵(heightmap)으로 돌아갑니다.

## 레이어가 바뀌면 (보이기, 단면) 알립니다.
signal layers_changed
## 회랑 지형의 높이맵을 바꿨을 때 (프랙탈 디테일) 알립니다. main.gd 가 걷는 플레이어를 올립니다.
signal terrain_rebuilt

const TERRAIN_SHADER := preload("res://shaders/cross_section.gdshader")
const SECTION_SHADER := preload("res://shaders/strata_section.gdshader")
const CAVE_SHADER := preload("res://shaders/cave.gdshader")
const WATER_SHADER := preload("res://shaders/water.gdshader")

## 레이어 순서와 화면 이름. solo 가 false 면 '만 보기' 대상이 아닌 선택입니다.
const LAYERS := {
	"terrain": {"label": "회랑 지형 (2 m)", "solo": true},
	"surround": {"label": "주변 지형 (25 m)", "solo": true},
	"water": {"label": "호수·강 수면", "solo": true},
	"water_table": {"label": "지하수면", "solo": true},
	"caves": {"label": "동굴", "solo": true},
	"section": {"label": "지층 단면", "solo": true},
	"entrance_holes": {"label": "동굴 입구 구멍", "solo": false},
	"fractal_detail": {
		"label": "프랙탈 디테일", "solo": false,
		"hint": "히어로 격자보다 짧은 파장(100 m 아래)의 거칠기를 굽기에서 이어 붙인 지표입니다.\n"
				+ "보기용이며 솔버 결과가 아닙니다. 끄면 기본 회랑 지표로 돌아갑니다."},
}
## 프랙탈 디테일을 더한 회랑 지표 높이맵과 그 지표의 동굴 입구 파일 (BakedPaths.resolve 로 바뀜).
const DETAIL_STEM := "res://baked/heightmap_detail"
const CAVE_MOUTH_NAME := "cave_mouth"
const CAVE_MOUTH_DETAIL_NAME := "cave_mouth_detail"
## 굽기의 '물 없음' 값은 -10000 m 입니다. 이보다 낮으면 물이 없는 표본입니다.
const WATER_NONE_BELOW_M := -9999.0
const WATER_COLOR := Color(0.10, 0.30, 0.42, 0.78)
const WATER_TABLE_COLOR := Color(0.20, 0.75, 0.95, 0.35)
## 단면 판의 위아래 여유 (m).
const SECTION_MARGIN_M := 20.0
## 자르는 면을 끌 때의 거리 (아무것도 버리지 않음).
const NO_CUT_OFFSET_M := 1.0e9

## manifest 를 읽고 레이어를 만들었는지.
var loaded := false
## 읽은 폴더와 manifest.
var baked_dir: String = ""
var manifest: Dictionary = {}
## 엔진 Y + y_offset_m = 해발 고도 (m).
var y_offset_m: float = 0.0
## 회랑 사각형 (X, Z).
var corridor_rect := Rect2()
## 재질 부피 (없으면 null).
var strata: StrataVolume
## 지하수면 높이맵 (없으면 null).
var water_table: HeightmapLoader
## 동굴 입구: 통로 한가운데 끝과 산비탈 밖 끝 (엔진 좌표).
var entrances_inside := PackedVector3Array()
var entrances_outside := PackedVector3Array()
## 동굴 메시 삼각형 수와 불러온 방법 ("import" 또는 "gltf_runtime").
var cave_faces: int = 0
var cave_source: String = ""
## 단면이 켜져 있는지, 자르는 면의 한 점과 법선 (카메라 쪽).
var section_on := false
var section_point := Vector3.ZERO
var section_normal := Vector3.BACK
## 레이어를 켤 때 단면을 어디에 둘지 정하는 함수: func() -> [점, 법선]. main.gd 가 넣습니다.
var section_anchor: Callable
## 단계별로 걸린 시간 (ms).
var load_ms := {}

var _terrain: HeightmapTerrain
var _surround: HeightmapTerrain
var _water: MeshInstance3D
var _water_table: MeshInstance3D
var _caves: Node3D
var _section: MeshInstance3D
var _section_y_center: float = 0.0
## 기본(디테일 없는) 회랑 높이맵. 디테일을 켤 때 높이 차를 재려고 한 번 읽습니다.
var _base_heightmap: HeightmapLoader
## 단면(자르는 면)을 받는 재질.
var _cut_materials: Array[ShaderMaterial] = []
var _terrain_material: ShaderMaterial
var _visible := {}
var _entrance_holes := true
## 회랑 지형의 기본 높이맵 경로 (setup 때의 Terrain.heightmap_stem).
var _base_stem := ""
## 높이맵 경로 → 셰이더용 높이 텍스처. 프랙탈 디테일을 켜고 끌 때 다시 만들지 않습니다.
var _textures := {}
## 동굴 입구 파일 이름 → 읽은 높이맵.
var _mouths := {}


## manifest 를 읽고 레이어를 만듭니다. 구운 묶음이 없으면 false (표본 지형만 보입니다).
func setup(terrain: HeightmapTerrain) -> bool:
	_terrain = terrain
	_base_stem = terrain.heightmap_stem
	_terrain_material = terrain.material as ShaderMaterial
	if _terrain_material != null:
		_cut_materials.append(_terrain_material)
	_visible["terrain"] = true
	baked_dir = BakedPaths.dir()
	var man_path := baked_dir.path_join("manifest.json")
	if not FileAccess.file_exists(man_path):
		print("구운 묶음이 없습니다 (%s). 표본 지형만 보입니다" % man_path)
		return false
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(man_path))
	if not (parsed is Dictionary) or parsed.get("format") != "bpcg-corridor":
		push_error("회랑 manifest 형식이 아닙니다: %s" % man_path)
		return false
	manifest = parsed
	y_offset_m = float(manifest["frame"]["y_offset_m"])
	var re: Dictionary = manifest["corridor"]["rect_engine_m"]
	corridor_rect = Rect2(
			float(re["x_min"]), float(re["z_min"]),
			float(re["x_max"]) - float(re["x_min"]), float(re["z_max"]) - float(re["z_min"]))

	var t := Time.get_ticks_msec()
	strata = StrataVolume.load_dir(baked_dir)
	if not strata.is_valid():
		push_warning("재질 부피를 쓰지 않습니다: " + strata.error_message)
		strata = null
	_lap("strata", t)
	if _terrain_material != null:
		_terrain_material.set_shader_parameter("color_mode", 1)
		_terrain_material.set_shader_parameter("y_offset_m", y_offset_m)
		_terrain_material.set_shader_parameter("open_entrances", _entrance_holes)
		if strata != null and terrain.used_stem == BakedPaths.resolve(terrain.heightmap_stem):
			strata.apply_to(_terrain_material)
		_apply_cave_mouth(CAVE_MOUTH_NAME)

	t = Time.get_ticks_msec()
	_build_surround()
	_lap("surround", t)
	t = Time.get_ticks_msec()
	_build_water()
	water_table = HeightmapLoader.load_stem(baked_dir.path_join("water_table"))
	if not water_table.is_valid():
		water_table = null
	else:
		_visible["water_table"] = false
	_lap("water", t)
	t = Time.get_ticks_msec()
	_build_caves()
	_lap("caves", t)
	_build_section()
	_read_entrances()
	set_section(false)
	_setup_fractal_detail()
	loaded = true
	print("구운 레이어: %s, 시간 %s ms" % [", ".join(layer_ids()), load_ms])
	return true


## 지금 있는 레이어 이름 (LAYERS 순서).
func layer_ids() -> Array[String]:
	var ids: Array[String] = []
	for id in LAYERS:
		if _visible.has(id):
			ids.append(id)
	return ids


func layer_label(id: String) -> String:
	return LAYERS[id]["label"]


func can_solo(id: String) -> bool:
	return LAYERS[id]["solo"]


## 패널 버튼에 띄울 설명 (없으면 빈 글).
func layer_hint(id: String) -> String:
	return LAYERS[id].get("hint", "")


func is_layer_visible(id: String) -> bool:
	return _visible.get(id, false)


## 레이어 하나를 켜거나 끕니다. 없는 레이어면 아무것도 하지 않습니다.
func set_layer_visible(id: String, on: bool) -> void:
	if not _visible.has(id):
		return
	match id:
		"terrain":
			_terrain.set_mesh_visible(on)
		"surround":
			_surround.set_mesh_visible(on)
		"water":
			_water.visible = on
		"water_table":
			if on and _water_table == null:
				_build_water_table()
			if _water_table != null:
				_water_table.visible = on
		"caves":
			_caves.visible = on
		"section":
			if on and not section_on:
				var anchor: Array = section_anchor.call() if section_anchor.is_valid() else []
				if anchor.size() == 2:
					set_section(true, anchor[0], anchor[1])
					return
				var c := corridor_rect.get_center()
				set_section(true, Vector3(c.x, 0.0, c.y), Vector3.BACK)
				return
			if not on:
				set_section(false)
				return
		"entrance_holes":
			_entrance_holes = on
			if _terrain_material != null:
				_terrain_material.set_shader_parameter("open_entrances", on)
		"fractal_detail":
			on = _use_detail_surface(on)
	_visible[id] = on
	layers_changed.emit()


func toggle_layer(id: String) -> void:
	set_layer_visible(id, not is_layer_visible(id))


## 이 레이어만 보이고 '만 보기' 대상인 나머지는 숨깁니다.
func solo(id: String) -> void:
	for other in layer_ids():
		if can_solo(other):
			set_layer_visible(other, other == id)


## '만 보기' 대상 레이어를 모두 켭니다 (지하수면과 단면은 처음처럼 끔).
func show_all() -> void:
	for id in layer_ids():
		if can_solo(id):
			set_layer_visible(id, id != "water_table" and id != "section")


## 자르는 면을 켜거나 끕니다. normal 은 버릴 쪽(카메라 쪽)을 향합니다 (수평으로 맞춤).
func set_section(on: bool, point := Vector3.ZERO, normal := Vector3.BACK) -> void:
	var n := Vector3(normal.x, 0.0, normal.z)
	n = n.normalized() if n.length() > 1e-6 else Vector3.BACK
	section_on = on
	section_point = point
	section_normal = n
	var rect4 := Vector4(corridor_rect.position.x, corridor_rect.position.y,
			corridor_rect.end.x, corridor_rect.end.y)
	for m in _cut_materials:
		m.set_shader_parameter("plane_normal", n)
		m.set_shader_parameter("plane_offset_m", n.dot(point) if on else NO_CUT_OFFSET_M)
		m.set_shader_parameter("section_rect", rect4)
	if _section != null:
		_section.visible = on
		if on:
			_section.global_transform = Transform3D(
					Basis.looking_at(-n, Vector3.UP), Vector3(point.x, _section_y_center, point.z))
	if _visible.has("section"):
		_visible["section"] = on
	layers_changed.emit()


## 자르는 면을 법선 반대쪽(멀어지는 쪽)으로 distance_m 만큼 옮깁니다.
func move_section(distance_m: float) -> void:
	if section_on:
		set_section(true, section_point - section_normal * distance_m, section_normal)


## 지하수면 높이 (엔진 Y). 없거나 밖이면 NAN.
func water_table_y(p: Vector3) -> float:
	return water_table.height_at(p) if water_table != null else NAN


func _lap(key: String, start_ms: int) -> void:
	load_ms[key] = Time.get_ticks_msec() - start_ms


func _new_cut_material(shader: Shader) -> ShaderMaterial:
	var m := ShaderMaterial.new()
	m.shader = shader
	_cut_materials.append(m)
	return m


## 동굴 입구 구멍 파일 (file_name: cave_mouth 또는 cave_mouth_detail) 을 지형 재질에 넣습니다.
## 그 파일이 없으면 cave_mouth 를 씁니다. 둘 다 없으면 '입구 구멍' 선택이 없습니다.
func _apply_cave_mouth(file_name: String) -> void:
	var mouth := _load_mouth(file_name)
	if not mouth.is_valid() and file_name != CAVE_MOUTH_NAME:
		mouth = _load_mouth(CAVE_MOUTH_NAME)
	if not mouth.is_valid():
		return
	_terrain_material.set_shader_parameter("cave_mouth", _cached_texture(mouth))
	_terrain_material.set_shader_parameter("mouth_origin", Vector2(mouth.origin.x, mouth.origin.z))
	_terrain_material.set_shader_parameter("mouth_spacing_m", mouth.spacing_m)
	_terrain_material.set_shader_parameter("mouth_size", Vector2(mouth.width, mouth.height))
	_visible["entrance_holes"] = _entrance_holes


func _load_mouth(file_name: String) -> HeightmapLoader:
	if not _mouths.has(file_name):
		_mouths[file_name] = HeightmapLoader.load_stem(baked_dir.path_join(file_name))
	return _mouths[file_name]


## 프랙탈 디테일 높이맵이 있고 회랑 지형이 구운 높이맵이면 선택 레이어로 두고 켭니다.
func _setup_fractal_detail() -> void:
	if _terrain.used_stem != BakedPaths.resolve(_base_stem):
		return
	if not HeightmapLoader.exists(BakedPaths.resolve(DETAIL_STEM)):
		return
	_visible["fractal_detail"] = false
	var t := Time.get_ticks_msec()
	set_layer_visible("fractal_detail", true)
	_lap("fractal_detail", t)


## 회랑 지형을 프랙탈 디테일 높이맵(on) 또는 기본 높이맵으로 바꿉니다. 지형 재질(색, 단면,
## 입구 구멍 켜기)과 메시가 보이는지는 그대로 두고, 동굴 입구 구멍과 단면 판의 지표 높이를
## 새 높이맵에 맞춥니다. 한 번 만든 메시·충돌 모양은 다시 씁니다. 반환: 실제로 켜졌는지.
func _use_detail_surface(on: bool) -> bool:
	var stem := DETAIL_STEM if on else _base_stem
	if _terrain.used_stem != BakedPaths.resolve(stem):
		var mesh_on := _terrain.is_mesh_visible()
		_terrain.heightmap_stem = stem
		if not _terrain.rebuild(true) or _terrain.used_stem != BakedPaths.resolve(stem):
			push_error("프랙탈 디테일 지형을 읽지 못해 기본 지형으로 돌아갑니다: %s" % stem)
			on = false
			_terrain.heightmap_stem = _base_stem
			_terrain.rebuild(true)
		_terrain.set_mesh_visible(mesh_on)
		terrain_rebuilt.emit()
	if _terrain_material != null:
		_apply_cave_mouth(CAVE_MOUTH_DETAIL_NAME if on else CAVE_MOUTH_NAME)
	_apply_section_surface()
	_apply_surface_offset(on)
	return on


## 프랙탈 디테일을 켜면 지형 색칠과 단면이 재질 부피의 층 깊이를 실제로 그린 지표에서 재도록
## 두 높이맵(지금, 기본)을 넘깁니다 (strata.gdshaderinc 의 surface_offset). 끄면 차이 0.
## 이것이 없으면 디테일이 지표를 낮춘 골에서 흙 아래 암석 색이 얼룩으로 보입니다.
func _apply_surface_offset(on: bool) -> void:
	var now := _terrain.heightmap
	if on and (_base_heightmap == null or not _base_heightmap.is_valid()):
		_base_heightmap = HeightmapLoader.load_stem(BakedPaths.resolve(_base_stem))
	var ok := on and now != null and _base_heightmap.is_valid() \
			and _base_heightmap.width == now.width and _base_heightmap.height == now.height
	var mats: Array[ShaderMaterial] = []
	if _terrain_material != null:
		mats.append(_terrain_material)
	if _section != null:
		mats.append(_section.material_override as ShaderMaterial)
	for m in mats:
		m.set_shader_parameter("has_surface_offset", ok)
		if ok:
			m.set_shader_parameter("surface_now", _cached_texture(now))
			m.set_shader_parameter("surface_base", _cached_texture(_base_heightmap))
			m.set_shader_parameter("offset_origin", now.origin)
			m.set_shader_parameter("offset_spacing_m", now.spacing_m)
			m.set_shader_parameter("offset_size", Vector2(now.width, now.height))


func _build_surround() -> void:
	var stem := baked_dir.path_join("surround25")
	if not HeightmapLoader.exists(stem):
		return
	var m := ShaderMaterial.new()
	m.shader = TERRAIN_SHADER
	m.set_shader_parameter("color_mode", 1)
	m.set_shader_parameter("y_offset_m", y_offset_m)
	m.set_shader_parameter("hole_rect", Vector4(corridor_rect.position.x,
			corridor_rect.position.y, corridor_rect.end.x, corridor_rect.end.y))
	_surround = HeightmapTerrain.new()
	_surround.name = "Surround"
	_surround.heightmap_stem = stem
	_surround.fallback_stem = ""
	_surround.material = m
	_surround.collision_hole = corridor_rect
	add_child(_surround)
	if _surround.heightmap != null and _surround.heightmap.is_valid():
		_visible["surround"] = true


func _build_water() -> void:
	var hm := HeightmapLoader.load_stem(baked_dir.path_join("water"))
	if not hm.is_valid():
		return
	_water = MeshInstance3D.new()
	_water.name = "Water"
	_water.mesh = _wet_quads(hm)
	_water.position = hm.origin
	_water.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var m := _new_cut_material(WATER_SHADER)
	m.set_shader_parameter("water_color", WATER_COLOR)
	_water.material_override = m
	add_child(_water)
	_visible["water"] = true


## 물이 있는 표본이 하나라도 닿는 칸마다 사각형을 만듭니다. 물이 없는 꼭짓점은 같은 칸의
## 물 높이 평균을 씁니다 (그 자리는 지표가 더 높아 가려집니다). 물이 없으면 빈 메시.
func _wet_quads(hm: HeightmapLoader) -> ArrayMesh:
	var w := hm.width
	var h := hm.height
	var hs := hm.heights
	var quads := {}
	for i in hs.size():
		if hs[i] <= WATER_NONE_BELOW_M:
			continue
		var row := i / w
		var col := i % w
		for dr in [-1, 0]:
			for dc in [-1, 0]:
				var r: int = row + dr
				var c: int = col + dc
				if r >= 0 and c >= 0 and r < h - 1 and c < w - 1:
					quads[r * w + c] = true
	var verts := PackedVector3Array()
	var indices := PackedInt32Array()
	var s := hm.spacing_m
	for q in quads:
		var row: int = q / w
		var col: int = q % w
		var ids := [q, q + 1, q + w, q + w + 1]
		var sum := 0.0
		var wet := 0
		for id in ids:
			if hs[id] > WATER_NONE_BELOW_M:
				sum += hs[id]
				wet += 1
		var fill := sum / float(wet)
		var base := verts.size()
		for k in 4:
			var y: float = hs[ids[k]] if hs[ids[k]] > WATER_NONE_BELOW_M else fill
			verts.append(Vector3(float(col + k % 2) * s, y, float(row + k / 2) * s))
		# 위에서 볼 때 시계 방향 (지형과 같음): 북서, 북동, 남서 / 북동, 남동, 남서
		indices.append_array(PackedInt32Array([base, base + 1, base + 2, base + 1, base + 3, base + 2]))
	var mesh := ArrayMesh.new()
	if verts.is_empty():
		return mesh
	var normals := PackedVector3Array()
	normals.resize(verts.size())
	normals.fill(Vector3.UP)
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_INDEX] = indices
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _build_water_table() -> void:
	if water_table == null:
		return
	_water_table = MeshInstance3D.new()
	_water_table.name = "WaterTable"
	_water_table.mesh = water_table.build_mesh()
	_water_table.position = water_table.origin
	_water_table.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var m := _new_cut_material(WATER_SHADER)
	m.set_shader_parameter("water_color", WATER_TABLE_COLOR)
	m.set_shader_parameter("roughness", 0.3)
	_water_table.material_override = m
	add_child(_water_table)
	set_section(section_on, section_point, section_normal)


func _build_caves() -> void:
	var path := baked_dir.path_join("caves.glb")
	if not FileAccess.file_exists(path):
		return
	var root: Node = null
	# 편집기가 가져온(import) 파일이면 빠르게 읽고, 아니면 실행 중에 glTF 를 읽습니다.
	if path.begins_with("res://") and ResourceLoader.exists(path):
		var packed := load(path) as PackedScene
		if packed != null:
			root = packed.instantiate()
			cave_source = "import"
	if root == null:
		var doc := GLTFDocument.new()
		var state := GLTFState.new()
		var err := doc.append_from_file(path, state)
		if err == OK:
			root = doc.generate_scene(state)
			cave_source = "gltf_runtime"
	if root == null or not (root is Node3D):
		push_error("동굴 메시를 읽지 못했습니다: %s" % path)
		return
	var m := _new_cut_material(CAVE_SHADER)
	if strata != null:
		m.set_shader_parameter("palette", strata.palette)
		m.set_shader_parameter("use_palette", true)
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mi := node as MeshInstance3D
		mi.material_override = m
		mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		for s in mi.mesh.get_surface_count():
			cave_faces += mi.mesh.surface_get_array_index_len(s) / 3
	_caves = root
	_caves.name = "Caves"
	add_child(_caves)
	_visible["caves"] = true


func _build_section() -> void:
	var surface := _terrain.heightmap if _terrain != null else null
	if strata == null or surface == null or not surface.is_valid():
		return
	var m := ShaderMaterial.new()
	m.shader = SECTION_SHADER
	strata.apply_to(m)
	if water_table != null and water_table.width == surface.width \
			and water_table.height == surface.height:
		m.set_shader_parameter("water_table", _height_texture(water_table))
	else:
		# 지하수면이 없으면 아주 깊게 두어 선과 물들이기가 생기지 않게 합니다.
		var deep := HeightmapLoader.new()
		deep.width = 1
		deep.height = 1
		deep.heights = PackedFloat32Array([-1.0e6])
		m.set_shader_parameter("water_table", _height_texture(deep))
	_section = MeshInstance3D.new()
	_section.name = "Section"
	_section.mesh = QuadMesh.new()
	_section.material_override = m
	_section.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_section.visible = false
	add_child(_section)
	_visible["section"] = false
	_apply_section_surface()


## 단면 판의 지표 높이 (지표 위를 버리고 깊이 선을 긋는 기준) 와 판의 위아래 범위를 지금 회랑
## 지형에 맞춥니다. 프랙탈 디테일은 지표를 기본보다 높일 수 있습니다.
func _apply_section_surface() -> void:
	if _section == null:
		return
	var surface := _terrain.heightmap
	var y_lo := surface.origin.y + strata.top.min_height_m - strata.depth_m() - SECTION_MARGIN_M
	var y_hi := surface.origin.y + surface.max_height_m + SECTION_MARGIN_M
	_section_y_center = 0.5 * (y_lo + y_hi)
	(_section.mesh as QuadMesh).size = Vector2(2.0 * corridor_rect.size.length(), y_hi - y_lo)
	if section_on:
		_section.global_position.y = _section_y_center
	var m := _section.material_override as ShaderMaterial
	m.set_shader_parameter("surface_height", _cached_texture(surface))
	m.set_shader_parameter("surface_origin", surface.origin)
	m.set_shader_parameter("surface_spacing_m", surface.spacing_m)
	m.set_shader_parameter("surface_size", Vector2(surface.width, surface.height))


## 높이맵 텍스처를 경로마다 한 번만 만듭니다.
func _cached_texture(hm: HeightmapLoader) -> ImageTexture:
	if not _textures.has(hm.stem):
		_textures[hm.stem] = _height_texture(hm)
	return _textures[hm.stem]


func _height_texture(hm: HeightmapLoader) -> ImageTexture:
	return ImageTexture.create_from_image(Image.create_from_data(
			hm.width, hm.height, false, Image.FORMAT_RF, hm.heights.to_byte_array()))


func _read_entrances() -> void:
	var path := baked_dir.path_join("entrances.json")
	if not FileAccess.file_exists(path):
		return
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not (parsed is Dictionary):
		return
	var inside: Variant = parsed.get("inside", [])
	var outside: Variant = parsed.get("outside", [])
	if not (inside is Array and outside is Array):
		push_warning("동굴 입구 목록이 배열이 아닙니다 (예전 굽기?): %s" % path)
		return
	for i in mini(inside.size(), outside.size()):
		entrances_inside.append(Vector3(inside[i][0], inside[i][1], inside[i][2]))
		entrances_outside.append(Vector3(outside[i][0], outside[i][1], outside[i][2]))
