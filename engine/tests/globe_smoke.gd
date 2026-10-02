extends SceneTree
## 지구본 연기 검사(smoke test). 화면 없이(--headless) 지구본 장면을 열어 핵심 경로를 봅니다.
##
## 실행 (저장소 맨 위에서, 스크립트를 바꾼 뒤에는 --import 를 먼저):[br]
##   godot --headless --path engine --import[br]
##   godot --headless --path engine --script res://tests/globe_smoke.gd -- --baked-dir=<폴더>[br]
## 지구본 자료는 <폴더>/globe/ 에서 읽습니다. 성공하면 'BPCG_GLOBE_OK' 를 찍고 quit(0),
## 실패하면 'BPCG_GLOBE_FAIL: <이유>' 를 찍고 quit(1) 합니다. 자료가 없으면 알림 화면이 뜨고
## 멈추지 않는지만 보고 'BPCG_GLOBE_OK (no data)' 를 찍습니다(globe.json 이 없으면 '자료가 없습니다',
## 있는데 잘못됐으면 '읽지 못했습니다' 알림). `-- --expect-globe` 를 주면 자료가 없을 때 실패합니다.
## tests/test_engine_globe.py 가 이 표시를 봅니다.
## [br]자료가 있으면 보는 것:[br]
## 1. 면 메시 6개, 면마다 꼭짓점 (N+1)² 개, 삼각형 2N² 개, 앞면이 바깥을 봄. 꼭짓점 (r, c) 의
##    UV = (c/N, r/N) 이고 방향이 문서의 사상과 같음 (대각선 밖 꼭짓점 포함: 행·열이 바뀌면 실패)[br]
## 2. 면 경계 꼭짓점이 이웃 면 꼭짓점과 비트 단위로 같고, 그 자리 모서리 고도도 같음 (틈 없음)[br]
## 3. globe.json 의 모든 필드를 고를 수 있음: 재질 텍스처가 바뀌고 범례 제목·버튼이 따라옴. 텍스처
##    색 = 색표(저장 값), 알파 = 텍셀의 종류(값 없음 0, color_break 아래 128, 그 밖 255).
##    point_markers 필드는 0 이 아닌 칸 수만큼 점을 찍고, 다른 필드로 바꾸면 점이 사라짐[br]
## 4. direction_to_cell ↔ cell_dir 왕복 (무작위 방향 200 개, 무작위 칸 200 개)[br]
## 5. 검사 안에서 따로 짠 역사상으로 읽은 .bin 값과 value_at 이 같음 (범주·연속 필드)[br]
## 6. 히어로 방향의 위도·경도가 globe.json 의 hero 와 0.01° 안에서 같고, 핀이 지표에 섬[br]
## 7. 고도 과장·강·호수·경위선·강 등급·그늘이 셰이더 uniform 과 패널 버튼에 반영됨 (키 입력 포함).
##    문턱 아래 강 칸을 가리키면 '그리지 않음' 이라고 읽음[br]
## 8. 카메라: 화면 가운데를 고르면 카메라 쪽 방향, 히어로로 날아가기, 확대·축소 한계[br]
## 9. 클릭 고정 상자가 모든 필드를 이름과 함께 보여 줌

const GLOBE_SCENE := "res://scenes/globe.tscn"
const SCRIPTS: Array[String] = [
	"res://scripts/globe_data.gd", "res://scripts/globe.gd", "res://scripts/globe_camera.gd",
	"res://scripts/globe_hud.gd", "res://scripts/globe_main.gd",
]
## 셰이더 → 꼭 있어야 하는 uniform (컴파일에 실패하면 uniform 목록이 비어 있습니다).
const SHADER_UNIFORMS := {
	"res://shaders/globe.gdshader": [
		"corner_elevation", "field_color", "overlay", "categorical", "face_res", "radius_m",
		"sea_level_m", "exaggeration", "show_rivers", "show_lakes", "show_grid", "river_colors",
		"river_min_class", "lake_color", "shade_relief", "light_dir_view", "relief_shading",
		"relief_shading_limit"],
	"res://shaders/atmosphere.gdshader": [
		"glow_color", "strength", "falloff", "globe_radius", "glow_radius"],
}
const EXPECT_ARG := "--expect-globe"
const ROUND_TRIP_SAMPLES := 200
const VALUE_SAMPLES := 300
## 위도·경도 허용 차 (도). globe.json 의 hero 는 같은 단위 벡터에서 계산한 값입니다.
const LAT_LON_TOLERANCE_DEG := 0.01
## 화면 가운데를 골랐을 때 카메라 방향과의 허용 각 (rad).
const PICK_TOLERANCE_RAD := 0.01
const WATCHDOG_MS := 90000

var _done := false
var _started_ms := 0
## 텍스처 픽셀 색을 직접 비교한 칸 수 (텍스처 그림을 읽을 수 없으면 0).
var _pixels_checked := 0
## 점(point_markers)을 확인한 필드 수.
var _marker_fields := 0


func _initialize() -> void:
	_started_ms = Time.get_ticks_msec()
	print("Godot %s | 지구본 폴더: %s" % [
		Engine.get_version_info().string, GlobeData.default_dir()])
	_run()


func _process(_delta: float) -> bool:
	if not _done and Time.get_ticks_msec() - _started_ms > WATCHDOG_MS:
		_fail("시간 제한 %d ms 를 넘었습니다" % WATCHDOG_MS)
	return false


func _run() -> void:
	await process_frame
	if not _check_scripts() or not _check_shaders():
		return
	var packed := load(GLOBE_SCENE) as PackedScene
	if packed == null:
		_fail("지구본 장면을 읽지 못했습니다: %s" % GLOBE_SCENE)
		return
	var main := packed.instantiate()
	root.add_child(main)
	await process_frame
	await process_frame
	var globe := main.get_node_or_null("Globe") as GlobeView
	var hud := main.get_node_or_null("Hud") as GlobeHud
	var camera := main.get_node_or_null("Camera") as Camera3D
	if globe == null or hud == null or camera == null:
		_fail("장면에 Globe(GlobeView), Hud(GlobeHud), Camera(Camera3D) 가 없습니다")
		return
	if not (main.get_node_or_null("WorldEnvironment") is WorldEnvironment):
		_fail("WorldEnvironment 가 없습니다")
		return
	var data: GlobeData = main.get("data")
	if data == null or not data.is_valid():
		await _check_no_data(main, hud, data)
		return
	if not _check_faces(globe, data):
		return
	if not _check_seams(globe, data):
		return
	if not _check_fields(globe, hud, data):
		return
	if not _check_round_trip(data):
		return
	if not _check_values(data):
		return
	if not _check_hero(globe, data):
		return
	if not await _check_display(main, globe, hud, data):
		return
	if not await _check_camera(main, globe, camera, data):
		return
	if not _check_pinned(main, hud, data):
		return
	_pass("")


## 스크립트가 구문 오류 없이 읽히는지 봅니다.
func _check_scripts() -> bool:
	for path in SCRIPTS:
		var script := load(path) as GDScript
		if script == null or not script.can_instantiate():
			return _fail("스크립트를 읽지 못했습니다 (구문 오류?): %s" % path)
	return true


func _check_shaders() -> bool:
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


## 자료가 없으면 알림이 보이고, 키를 눌러도 멈추지 않아야 합니다.
func _check_no_data(main: Node, hud: GlobeHud, data: GlobeData) -> void:
	if OS.get_cmdline_user_args().has(EXPECT_ARG):
		_fail("지구본 자료를 기대했지만 읽지 못했습니다: %s" % (
				"자료 없음" if data == null else data.error_message))
		return
	# globe.json 이 없으면 '없습니다', 있는데 잘못됐으면 '읽지 못했습니다' 알림이어야 합니다.
	var missing := data == null or data.missing
	var headline := "지구본 자료가 없습니다" if missing else "지구본 자료를 읽지 못했습니다"
	if not hud.is_missing_visible() or not hud.missing_text().begins_with(headline):
		_fail("자료 알림이 '%s' 로 시작하지 않습니다: '%s'" % [headline, hud.missing_text()])
		return
	if hud.is_panel_visible():
		_fail("자료가 없는데 레이어 패널이 보입니다")
		return
	for key in [KEY_1, KEY_R, KEY_K, KEY_L, KEY_BRACKETLEFT, KEY_BRACKETRIGHT, KEY_ESCAPE,
			KEY_SPACE, KEY_F, KEY_H, KEY_TAB]:
		_press(key)
	await process_frame
	await process_frame
	if not is_instance_valid(main) or main.get_parent() != root:
		_fail("자료가 없는 장면이 키 입력 뒤 사라졌습니다")
		return
	print("자료 없음 알림: %s" % hud.missing_text().get_slice("\n", 0))
	_pass(" (no data)")


func _check_faces(globe: GlobeView, data: GlobeData) -> bool:
	var n := data.face_res
	if globe.faces.size() != GlobeData.FACE_COUNT:
		return _fail("면 메시가 %d 개입니다 (6 개여야 함)" % globe.faces.size())
	for f in GlobeData.FACE_COUNT:
		var mi := globe.get_node_or_null("Face%d" % f) as MeshInstance3D
		if mi == null or mi != globe.faces[f] or not (mi.mesh is ArrayMesh):
			return _fail("Face%d 메시(ArrayMesh)가 없습니다" % f)
		var mesh := mi.mesh as ArrayMesh
		var vertex_count: int = mesh.surface_get_array_len(0)
		if vertex_count != (n + 1) * (n + 1):
			return _fail("면 %d 의 꼭짓점 %d 개가 (N+1)² = %d 와 다릅니다" % [
				f, vertex_count, (n + 1) * (n + 1)])
		if mesh.surface_get_array_index_len(0) != n * n * 6:
			return _fail("면 %d 의 인덱스 수가 6N² 가 아닙니다" % f)
		var m := mi.material_override as ShaderMaterial
		if m == null or m.shader == null \
				or m.shader.resource_path != "res://shaders/globe.gdshader":
			return _fail("면 %d 의 재질이 globe.gdshader 가 아닙니다" % f)
		var arrays := mesh.surface_get_arrays(0)
		var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
		# Plane(a, b, c) 는 시계 방향 점 순서로 법선을 정합니다. 앞면이 바깥을 봐야 합니다.
		var n_tris := i_div(indices.size(), 3)
		for t in [0, n_tris >> 1, n_tris - 1]:
			var a := vertices[indices[t * 3]]
			var b := vertices[indices[t * 3 + 1]]
			var c := vertices[indices[t * 3 + 2]]
			if Plane(a, b, c).normal.dot((a + b + c) / 3.0) <= 0.0:
				return _fail("면 %d 의 삼각형 %d 앞면이 안쪽을 봅니다" % [f, t])
		var half := vertex_count >> 1
		for i in [0, n, vertex_count - 1, half]:
			if absf(vertices[i].length() - 1.0) > 1e-5:
				return _fail("면 %d 꼭짓점 %d 이 단위 구 위에 있지 않습니다" % [f, i])
		# 꼭짓점 (r, c) 의 UV 는 (c/N, r/N), 방향은 corner_dir(f, r, c). 대각선 밖(r ≠ c) 꼭짓점이
		# 있어야 행·열이 바뀐 메시(UV 나 방향의 전치)를 잡습니다.
		var uvs: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]
		if uvs.size() != vertex_count:
			return _fail("면 %d 의 UV 가 %d 개입니다 (꼭짓점 %d 개)" % [f, uvs.size(), vertex_count])
		var side := n + 1
		for rc in [[0, 0], [0, n], [n, 0], [n, n], [1, mini(3, n)], [i_div(n, 2), i_div(n, 4)],
				[i_div(n, 2), i_div(n, 2)], [n - 1, 1]]:
			var r: int = rc[0]
			var c: int = rc[1]
			var want_uv := Vector2(float(c) / float(n), float(r) / float(n))
			if not uvs[r * side + c].is_equal_approx(want_uv):
				return _fail("면 %d 꼭짓점 (행 %d, 열 %d) 의 UV %s 가 (열/N, 행/N) = %s 가 아닙니다" % [
					f, r, c, uvs[r * side + c], want_uv])
			if vertices[r * side + c].distance_to(data.corner_dir(f, r, c)) > 1e-5:
				return _fail("면 %d 꼭짓점 (행 %d, 열 %d) 방향이 문서의 사상과 다릅니다" % [f, r, c])
	if globe.atmosphere == null:
		return _fail("대기 빛(Atmosphere) 이 없습니다")
	print("면 메시: 6 × (%d+1)² 꼭짓점, 앞면 바깥, UV = (열/N, 행/N)" % n)
	return true


## 면 경계 꼭짓점이 다른 면의 꼭짓점과 비트 단위로 같은 자리이고, 모서리 고도도 같은지 봅니다.
func _check_seams(globe: GlobeView, data: GlobeData) -> bool:
	var n := data.face_res
	var side := n + 1
	var seen := {}  # Vector3 → [면, 모서리 고도]
	var shared := 0
	var worst_m := 0.0
	for f in GlobeData.FACE_COUNT:
		var arrays := globe.faces[f].mesh.surface_get_arrays(0)
		var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		for r in side:
			for c in side:
				if r != 0 and r != n and c != 0 and c != n:
					continue
				var v := vertices[r * side + c]
				var z := data.corner_elevation_m[f * side * side + r * side + c]
				if seen.has(v):
					var other: Array = seen[v]
					if other[0] != f:
						shared += 1
						worst_m = maxf(worst_m, absf(z - float(other[1])))
				else:
					seen[v] = [f, z]
	# 면마다 둘레 4N 개, 모두 다른 면과 나눔. 큐브 꼭짓점 8 개는 세 면이 나눕니다.
	var boundary := 6 * 4 * n
	var unique := seen.size()
	if unique * 2 + 8 != boundary:
		return _fail("면 경계 꼭짓점이 이웃 면과 맞지 않습니다: 서로 다른 자리 %d 개 (기대 %d)" % [
			unique, i_div(boundary - 8, 2)])
	if worst_m > 1e-3:
		return _fail("이웃 면이 나누는 꼭짓점의 고도가 최대 %.3f m 다릅니다 (틈이 생김)" % worst_m)
	print("면 경계: 나눈 꼭짓점 %d 쌍, 고도 차 최대 %.4f m" % [shared, worst_m])
	return true


func _check_fields(globe: GlobeView, hud: GlobeHud, data: GlobeData) -> bool:
	if globe.active_field != data.default_field():
		return _fail("처음 고른 필드가 %s 입니다 (기대 %s)" % [globe.active_field, data.default_field()])
	var previous: Texture2D = null
	var names := data.field_names()
	for name in names:
		if not globe.set_field(name):
			return _fail("필드 '%s' 를 고르지 못했습니다" % name)
		var textures := data.field_textures(name)
		if textures.size() != GlobeData.FACE_COUNT:
			return _fail("필드 '%s' 의 텍스처가 %d 장입니다" % [name, textures.size()])
		for f in GlobeData.FACE_COUNT:
			var m := globe.materials[f]
			if m.get_shader_parameter("field_color") != textures[f]:
				return _fail("필드 '%s' 를 골랐는데 면 %d 재질 텍스처가 그대로입니다" % [name, f])
			if bool(m.get_shader_parameter("categorical")) != data.is_categorical(name):
				return _fail("필드 '%s' 의 categorical uniform 이 틀립니다" % name)
		if textures[0] == previous:
			return _fail("필드 '%s' 의 텍스처가 앞 필드와 같습니다" % name)
		previous = textures[0]
		if not hud.legend_title().begins_with(data.label_of(name)):
			return _fail("범례 제목 '%s' 이 필드 '%s' 와 다릅니다" % [hud.legend_title(),
				data.label_of(name)])
		var button := hud.field_button(name)
		if button == null or not button.button_pressed:
			return _fail("필드 '%s' 버튼이 눌린 상태가 아닙니다" % name)
		if data.is_categorical(name):
			if hud.legend_chip_count() != data.categories(name).size():
				return _fail("범주 필드 '%s' 범례 칸 %d 개가 범주 %d 개와 다릅니다" % [
					name, hud.legend_chip_count(), data.categories(name).size()])
			if hud.legend_area_note() != data.area_fraction_note(name):
				return _fail("범주 필드 '%s' 범례의 넓이 몫 글 '%s' 가 globe.json 과 다릅니다" % [
					name, hud.legend_area_note()])
		else:
			var ticks := hud.legend_ticks()
			if not hud.legend_bar_visible() or ticks[0].is_empty() or ticks[2].is_empty():
				return _fail("연속 필드 '%s' 의 색 막대나 최소·최대 글이 없습니다" % name)
		if str(data.field(name).get("description", "")).is_empty():
			return _fail("필드 '%s' 에 설명(description)이 없습니다" % name)
		if not _check_texture_colors(data, name, textures):
			return false
		if not _check_markers(globe, data, name):
			return false
	globe.set_field(data.default_field())
	if globe.marker_count != 0 or globe.point_markers != null:
		return _fail("점을 찍지 않는 필드로 바꿨는데 점 %d 개가 남았습니다" % globe.marker_count)
	print("필드 %d 개 모두 고름 (텍스처 픽셀 %d 개 색 확인, 점 찍은 필드 %d 개): %s" % [
		names.size(), _pixels_checked, _marker_fields, ", ".join(names)])
	return true


## point_markers 필드는 범주 0 도 '값 없음' 범주도 아닌 칸마다 점 하나 (그 밖의 필드는 점 없음).
## 칸 수는 globe.json 과 .bin 을 따로 읽어 셉니다.
func _check_markers(globe: GlobeView, data: GlobeData, name: String) -> bool:
	var entry := data.field(name)
	if not (entry.get("point_markers") == true):
		if globe.marker_count != 0 or globe.point_markers != null:
			return _fail("필드 '%s' 는 점을 찍지 않는데 점이 %d 개 있습니다" % [name, globe.marker_count])
		return true
	var skip := {0: true}
	for cat in entry.get("categories", []):
		if cat is Dictionary and cat.get("no_data") == true:
			skip[int(cat["id"])] = true
	var bytes := FileAccess.get_file_as_bytes(data.folder.path_join(str(entry["file"])))
	var want := 0
	for b in bytes:
		if not skip.has(b):
			want += 1
	if globe.marker_count != want or (want > 0) != (globe.point_markers != null):
		return _fail("필드 '%s' 의 점 %d 개가 0 이 아닌 칸 %d 개와 다릅니다" % [
			name, globe.marker_count, want])
	if want > 0:
		var mesh := globe.point_markers.mesh as ArrayMesh
		if mesh == null or mesh.surface_get_primitive_type(0) != Mesh.PRIMITIVE_POINTS \
				or mesh.surface_get_array_len(0) != want:
			return _fail("필드 '%s' 의 점 메시가 점 %d 개가 아닙니다" % [name, want])
		var points: PackedVector3Array = mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX]
		for p in [points[0], points[points.size() - 1]]:
			if p.length() < globe.surface_radius(p.normalized()):
				return _fail("필드 '%s' 의 점이 지표 아래에 있습니다: %s" % [name, p])
		if entry.has("n_nonzero_cells") and int(entry["n_nonzero_cells"]) != want:
			return _fail("globe.json 의 n_nonzero_cells %s 가 셈 %d 과 다릅니다" % [
				entry["n_nonzero_cells"], want])
	_marker_fields += 1
	return true


## 텍스처의 픽셀 색이 color_of(저장 값) 과 같은지 봅니다 (텍스처 그림을 읽을 수 있을 때만).
func _check_texture_colors(data: GlobeData, name: String,
		textures: Array[ImageTexture]) -> bool:
	var rng := RandomNumberGenerator.new()
	rng.seed = 7
	for k in 20:
		var f := rng.randi_range(0, 5)
		var image := textures[f].get_image()
		if image == null or image.is_empty():
			return true
		var r := rng.randi_range(0, data.face_res - 1)
		var c := rng.randi_range(0, data.face_res - 1)
		var value := data.raw_value(name, f, r, c)
		var want := data.color_of(name, value)
		var got := image.get_pixel(c, r)
		_pixels_checked += 1
		if absf(got.r - want.r) + absf(got.g - want.g) + absf(got.b - want.b) > 3.5 / 255.0:
			return _fail("필드 '%s' 텍스처 (면 %d, 행 %d, 열 %d) 색 %s 가 %s 와 다릅니다" % [
				name, f, r, c, got, want])
		# 알파 = 텍셀의 종류. globe.json 의 color_break 로 따로 계산합니다.
		var kind := 255
		if not data.is_categorical(name):
			var brk: Variant = data.field(name).get("color_break")
			if is_nan(value):
				kind = 0
			elif (brk is float or brk is int) and value < float(brk):
				kind = 128
		if got.a8 != kind or data.texel_kind(name, value) != kind:
			return _fail("필드 '%s' 텍스처 (면 %d, 행 %d, 열 %d) 알파 %d 가 종류 %d 가 아닙니다" % [
				name, f, r, c, got.a8, kind])
	return true


func _check_round_trip(data: GlobeData) -> bool:
	var rng := RandomNumberGenerator.new()
	rng.seed = 2026
	var n := data.face_res
	# 칸 하나의 각 크기는 면 가운데에서 (π/2)/N 이고 어디서나 그 1.5 배를 넘지 않습니다.
	var max_angle := 1.5 * sqrt(2.0) * (PI / 2.0) / float(n)
	var worst := 0.0
	for i in ROUND_TRIP_SAMPLES:
		var d := Vector3(rng.randfn(), rng.randfn(), rng.randfn()).normalized()
		var cell := data.direction_to_cell(d)
		var center := data.cell_dir(cell["face"], cell["row"], cell["col"])
		var back := data.direction_to_cell(center)
		if back["face"] != cell["face"] or back["row"] != cell["row"] or back["col"] != cell["col"]:
			return _fail("왕복 실패: 방향 %s → 칸 %s → 중심 %s → 칸 %s" % [d, cell, center, back])
		var angle := d.angle_to(center)
		worst = maxf(worst, angle)
		if angle > max_angle:
			return _fail("방향 %s 와 그 칸 중심의 각 %.5f rad 가 칸 크기 %.5f 보다 큽니다" % [
				d, angle, max_angle])
	for i in ROUND_TRIP_SAMPLES:
		var f := rng.randi_range(0, 5)
		var r := rng.randi_range(0, n - 1)
		var c := rng.randi_range(0, n - 1)
		var cell := data.direction_to_cell(data.cell_dir(f, r, c))
		if cell["face"] != f or cell["row"] != r or cell["col"] != c:
			return _fail("칸 (%d, %d, %d) 중심이 칸 %s 로 돌아왔습니다" % [f, r, c, cell])
	print("왕복: 방향 %d 개, 칸 %d 개, 중심까지 최대 %.4f rad" % [
		ROUND_TRIP_SAMPLES, ROUND_TRIP_SAMPLES, worst])
	return true


## globe.json 을 따로 읽고 역사상을 따로 짜서 .bin 의 값과 value_at 을 비교합니다.
func _check_values(data: GlobeData) -> bool:
	var meta: Dictionary = JSON.parse_string(
			FileAccess.get_file_as_string(data.folder.path_join(GlobeData.MANIFEST)))
	var n := int(meta["face_res"])
	var normals: Array[Vector3] = []
	var us: Array[Vector3] = []
	var vs: Array[Vector3] = []
	normals.resize(6)
	us.resize(6)
	vs.resize(6)
	for face in meta["faces"]:
		var k := int(face["index"])
		normals[k] = Vector3(face["n"][0], face["n"][1], face["n"][2])
		us[k] = Vector3(face["u"][0], face["u"][1], face["u"][2])
		vs[k] = Vector3(face["v"][0], face["v"][1], face["v"][2])
	var categorical := ""
	var continuous := ""
	for f in meta["fields"]:
		if f["kind"] == "categorical" and categorical.is_empty():
			categorical = f["name"]
		if f["kind"] == "continuous" and f["dtype"] == "float32" and continuous.is_empty():
			continuous = f["name"]
	var rng := RandomNumberGenerator.new()
	rng.seed = 99
	var checked := 0
	for name in [categorical, continuous]:
		if name.is_empty():
			continue
		var entry := data.field(name)
		var bytes := FileAccess.get_file_as_bytes(data.folder.path_join(entry["file"]))
		var floats: Variant = bytes.to_float32_array() if entry["dtype"] == "float32" else null
		for i in VALUE_SAMPLES:
			var d := Vector3(rng.randfn(), rng.randfn(), rng.randfn()).normalized()
			var face := 0
			for k in 6:
				if d.dot(normals[k]) > d.dot(normals[face]):
					face = k
			var dn := d.dot(normals[face])
			var a := 4.0 / PI * atan(d.dot(us[face]) / dn)
			var b := 4.0 / PI * atan(d.dot(vs[face]) / dn)
			var col := clampi(floori((a + 1.0) * n / 2.0), 0, n - 1)
			var row := clampi(floori((b + 1.0) * n / 2.0), 0, n - 1)
			var index := (face * n + row) * n + col
			var want: float = float(bytes[index]) if floats == null else float(floats[index])
			var got := data.value_at(name, d)
			var same: bool = (is_nan(want) and is_nan(got)) or want == got
			if not same:
				return _fail("value_at('%s', %s) = %s 가 .bin 의 칸 (%d, %d, %d) 값 %s 와 다릅니다" % [
					name, d, got, face, row, col, want])
			checked += 1
	print("value_at: %s, %s 의 %d 칸이 .bin 과 같음" % [categorical, continuous, checked])
	return true


func _check_hero(globe: GlobeView, data: GlobeData) -> bool:
	if data.hero.is_empty():
		if globe.hero_marker != null:
			return _fail("히어로가 없는데 핀이 있습니다")
		print("히어로: globe.json 에 없음")
		return true
	var dir := data.hero_direction()
	var ll := GlobeData.lat_lon(dir)
	var want := Vector2(float(data.hero["lat_deg"]), float(data.hero["lon_deg"]))
	var d_lat := absf(ll.x - want.x)
	var d_lon := absf(wrapf(ll.y - want.y, -180.0, 180.0)) * cos(deg_to_rad(want.x))
	if d_lat > LAT_LON_TOLERANCE_DEG or d_lon > LAT_LON_TOLERANCE_DEG:
		return _fail("히어로 위도·경도 %s 가 globe.json 의 %s 와 다릅니다" % [ll, want])
	var back := GlobeData.direction_from_lat_lon(ll.x, ll.y)
	if back.distance_to(dir) > 1e-5:
		return _fail("direction_from_lat_lon 이 lat_lon 의 역이 아닙니다: %s ≠ %s" % [back, dir])
	if globe.hero_marker == null or globe.hero_label == null:
		return _fail("히어로 핀이나 이름표가 없습니다")
	var radius := globe.hero_marker.position.length()
	if absf(radius - globe.surface_radius(dir)) > 1e-4:
		return _fail("히어로 핀이 지표에 있지 않습니다: 반지름 %f" % radius)
	if globe.hero_marker.global_basis.y.normalized().dot(dir) < 0.9999:
		return _fail("히어로 핀이 지표에 수직이 아닙니다")
	if not globe.hero_label.text.contains("히어로 유역"):
		return _fail("히어로 이름표 글이 '%s' 입니다" % globe.hero_label.text)
	print("히어로: %s, 이름표 '%s'" % [GlobeData.format_lat_lon(ll), globe.hero_label.text])
	return true


func _check_display(main: Node, globe: GlobeView, hud: GlobeHud, data: GlobeData) -> bool:
	var m := globe.materials[0]
	globe.set_exaggeration(50.0)
	if not is_equal_approx(float(m.get_shader_parameter("exaggeration")), 50.0):
		return _fail("고도 과장 50 이 셰이더에 들어가지 않았습니다")
	globe.step_exaggeration(1)
	if not is_equal_approx(globe.exaggeration, 75.0):
		return _fail("고도 과장 한 단계 위가 %f 입니다 (기대 75)" % globe.exaggeration)
	globe.set_exaggeration(1000.0)
	if globe.exaggeration != GlobeView.MAX_EXAGGERATION:
		return _fail("고도 과장이 100 을 넘었습니다: %f" % globe.exaggeration)
	globe.set_exaggeration(0.0)
	if globe.exaggeration != GlobeView.MIN_EXAGGERATION:
		return _fail("고도 과장이 1 보다 작아졌습니다: %f" % globe.exaggeration)
	if globe.hero_marker != null:
		var dir := data.hero_direction()
		if absf(globe.hero_marker.position.length() - globe.surface_radius(dir)) > 1e-4:
			return _fail("고도 과장을 바꿨는데 히어로 핀이 따라오지 않았습니다")
	globe.set_exaggeration(GlobeView.DEFAULT_EXAGGERATION)
	_press(KEY_BRACKETRIGHT)
	if not is_equal_approx(float(m.get_shader_parameter("exaggeration")), 30.0):
		return _fail("] 키로 고도 과장이 30 이 되지 않았습니다: %s" % m.get_shader_parameter(
				"exaggeration"))
	_press(KEY_BRACKETLEFT)
	if not is_equal_approx(globe.exaggeration, GlobeView.DEFAULT_EXAGGERATION):
		return _fail("[ 키로 고도 과장이 돌아오지 않았습니다")

	var checks := {
		"show_rivers": [KEY_R, GlobeView.OVERLAY_RIVERS],
		"show_lakes": [KEY_K, GlobeView.OVERLAY_LAKES],
		"show_grid": [KEY_L, ""],
	}
	for uniform_name in checks:
		var key: Key = checks[uniform_name][0]
		var overlay: String = checks[uniform_name][1]
		if not overlay.is_empty() and not data.has_overlay(overlay):
			continue
		var before := bool(m.get_shader_parameter(uniform_name))
		_press(key)
		var after := bool(m.get_shader_parameter(uniform_name))
		if after == before:
			return _fail("키 %s 로 %s 가 바뀌지 않았습니다" % [OS.get_keycode_string(key), uniform_name])
		var button: BaseButton = hud.grid_button() if overlay.is_empty() \
				else hud.overlay_button(overlay)
		if button == null or button.button_pressed != after:
			return _fail("%s 버튼 상태가 uniform 과 다릅니다" % uniform_name)
		_press(key)
		if bool(m.get_shader_parameter(uniform_name)) != before:
			return _fail("키 %s 를 두 번 눌렀는데 %s 가 돌아오지 않았습니다" % [
				OS.get_keycode_string(key), uniform_name])
	if data.has_overlay(GlobeView.OVERLAY_RIVERS):
		var before := int(m.get_shader_parameter("river_min_class"))
		_press(KEY_R, true)
		var after := int(m.get_shader_parameter("river_min_class"))
		if after == before or after != globe.river_min_class:
			return _fail("Shift+R 로 강 등급 문턱이 바뀌지 않았습니다 (%d → %d)" % [before, after])
		if not _check_river_readout(globe, hud, data):
			return false
		globe.set_river_min_class(GlobeView.DEFAULT_RIVER_MIN_CLASS)

	# G: 그늘·대기 빛 (꾸밈) 켜기·끄기
	var shade_before := bool(m.get_shader_parameter("shade_relief"))
	_press(KEY_G)
	var shade_after := bool(m.get_shader_parameter("shade_relief"))
	if shade_after == shade_before or shade_after != globe.show_shading:
		return _fail("G 키로 그늘(shade_relief)이 바뀌지 않았습니다")
	if globe.atmosphere.visible != shade_after:
		return _fail("G 키로 그늘을 바꿨는데 대기 빛이 따라오지 않았습니다")
	if hud.shading_button() == null or hud.shading_button().button_pressed != shade_after:
		return _fail("그늘 버튼 상태가 uniform 과 다릅니다")
	_press(KEY_G)
	if bool(m.get_shader_parameter("shade_relief")) != shade_before:
		return _fail("G 키를 두 번 눌렀는데 그늘이 돌아오지 않았습니다")

	# 숫자 키: 2 번째 필드, 패널·도움말 숨기기
	var ordered := data.ordered_field_names()
	if ordered.size() >= 2:
		_press(KEY_2)
		if globe.active_field != ordered[1]:
			return _fail("2 키로 '%s' 가 아니라 '%s' 를 골랐습니다" % [ordered[1], globe.active_field])
		_press(KEY_1)
	_press(KEY_TAB)
	if hud.is_panel_visible():
		return _fail("Tab 으로 패널이 숨지 않았습니다")
	_press(KEY_TAB)
	if not hud.is_panel_visible():
		return _fail("Tab 을 두 번 눌렀는데 패널이 보이지 않습니다")
	await process_frame
	print("표시: 고도 과장, 강·호수·경위선, 강 등급, 그늘, 숫자 키, 패널 숨기기 확인")
	return true


## 문턱 아래 강 칸을 가리키면 마우스 자리 글에 '그리지 않음' 이 붙고, 문턱 위면 붙지 않습니다.
func _check_river_readout(globe: GlobeView, hud: GlobeHud, data: GlobeData) -> bool:
	var rivers := FileAccess.get_file_as_bytes(data.folder.path_join(
			str(data.overlay(GlobeView.OVERLAY_RIVERS)["file"])))
	var low := 256
	for b in rivers:
		if b > 0 and b < low:
			low = b
	if low == 256:
		return true
	# 가장 낮은 등급 칸 하나: 문턱을 그 등급으로 두면 그리고, 그 위로 올리면 그리지 않음
	var index := rivers.find(low)
	var nn := data.face_res * data.face_res
	var face := i_div(index, nn)
	var row := i_div(index - face * nn, data.face_res)
	var col := index - face * nn - row * data.face_res
	var dir := data.cell_dir(face, row, col)
	if data.overlay_value_at(GlobeView.OVERLAY_RIVERS, dir) != low:
		return _fail("강 칸 (%d, %d, %d) 의 중심이 그 칸으로 돌아오지 않습니다" % [face, row, col])
	globe.set_overlay_visible(GlobeView.OVERLAY_RIVERS, true)
	globe.set_river_min_class(low)
	if hud.describe_point(dir).contains("그리지 않음"):
		return _fail("문턱 %d 등급인데 %d 등급 강 칸을 '그리지 않음' 이라고 읽습니다" % [low, low])
	if low >= globe.max_river_class():
		print("강 읽기: 가장 높은 등급 %d 만 있어 문턱 위로 올려 볼 수 없습니다" % low)
		return true
	globe.set_river_min_class(low + 1)
	var text := hud.describe_point(dir)
	if not text.contains("그리지 않음"):
		return _fail("문턱 %d 등급 아래 강 칸을 그리는 것처럼 읽습니다:\n%s" % [low + 1, text])
	print("강 읽기: 문턱(%d 등급) 아래 %d 등급 칸은 '그리지 않음'" % [low + 1, low])
	return true


func _check_camera(main: Node, globe: GlobeView, camera: Camera3D, data: GlobeData) -> bool:
	await process_frame
	var center := Vector2(root.size) * 0.5
	var picked := globe.pick(camera, center)
	var toward := camera.global_position.normalized()
	if picked == Vector3.ZERO or picked.angle_to(toward) > PICK_TOLERANCE_RAD:
		return _fail("화면 가운데를 골랐는데 %s 입니다 (카메라 쪽 %s)" % [picked, toward])
	camera.call("zoom_by", -100.0)
	if not is_equal_approx(float(camera.get("distance")), 1.15):
		return _fail("가장 가까이 당겼는데 거리가 %s 입니다" % camera.get("distance"))
	camera.call("zoom_by", 100.0)
	if not is_equal_approx(float(camera.get("distance")), 8.0):
		return _fail("가장 멀리 밀었는데 거리가 %s 입니다" % camera.get("distance"))
	var target := data.hero_direction()
	if target == Vector3.ZERO:
		target = GlobeData.direction_from_lat_lon(37.5, 127.0)
	camera.call("fly_to", target, 0.0)
	await process_frame
	if camera.global_position.normalized().angle_to(target) > 1e-3:
		return _fail("fly_to 뒤 카메라가 %s 쪽에 있지 않습니다" % target)
	picked = globe.pick(camera, center)
	if picked.angle_to(target) > PICK_TOLERANCE_RAD:
		return _fail("fly_to 뒤 화면 가운데가 %s 가 아니라 %s 입니다" % [target, picked])
	_press(KEY_SPACE)
	if not bool(camera.get("auto_rotate")):
		return _fail("Space 로 자동 회전이 켜지지 않았습니다")
	_press(KEY_SPACE)
	print("카메라: 가운데 고르기, 확대 한계 1.15 ~ 8, 날아가기, 자동 회전 확인")
	return true


func _check_pinned(main: Node, hud: GlobeHud, data: GlobeData) -> bool:
	var dir := data.hero_direction()
	if dir == Vector3.ZERO:
		dir = GlobeData.direction_from_lat_lon(10.0, 20.0)
	main.call("pin", dir)
	var text := hud.pinned_text()
	for name in data.field_names():
		if not text.contains(data.label_of(name) + ": "):
			return _fail("고정 상자에 '%s' 가 없습니다:\n%s" % [data.label_of(name), text])
	for o in data.overlays:
		if not text.contains(str(o.get("label", o["name"])) + ": "):
			return _fail("고정 상자에 겹쳐 보기 '%s' 가 없습니다" % o["name"])
	var hover := hud.describe_point(dir)
	if not hover.contains("위도·경도") or not hover.contains(data.label_of(data.default_field())):
		return _fail("마우스 자리 글에 위도·경도나 고도가 없습니다:\n%s" % hover)
	_press(KEY_ESCAPE)
	if not hud.pinned_text().is_empty():
		return _fail("Esc 로 고정이 풀리지 않았습니다")
	print("고정 상자: 필드 %d 개와 겹쳐 보기 %d 개를 보여 줌" % [
		data.field_names().size(), data.overlays.size()])
	return true


func _press(key: Key, shift: bool = false) -> void:
	var event := InputEventKey.new()
	event.physical_keycode = key
	event.keycode = key
	event.shift_pressed = shift
	event.pressed = true
	root.push_input(event)
	var release := event.duplicate() as InputEventKey
	release.pressed = false
	root.push_input(release)


static func i_div(a: int, b: int) -> int:
	return floori(float(a) / float(b))


func _pass(suffix: String) -> void:
	_done = true
	print("BPCG_GLOBE_OK" + suffix)
	quit(0)


func _fail(reason: String) -> bool:
	_done = true
	print("BPCG_GLOBE_FAIL: " + reason)
	quit(1)
	return false
