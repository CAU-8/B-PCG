class_name GlobeView
extends Node3D
## 지구본: 큐브스피어 면 6장을 고도만큼 부풀려 그리고, 고른 필드의 색을 칠합니다.
##
## setup(data) 를 부르면 면 메시 6개(Face0~Face5), 대기 빛(Atmosphere), 히어로 표시(Hero)를
## 자식으로 만듭니다. point_markers 인 범주 필드(드문 동굴 칸 등)를 고르면 그 칸마다 점(Markers)도
## 찍습니다. 반지름 1 이 해수면이고, 좌표는 GlobeData 의 약속(Godot +Y = 북쪽)을 따릅니다.
## [br]면 f 의 메시는 모서리 (N+1)² 개의 단위 방향입니다. 셰이더(globe.gdshader)가 모서리 고도
## 텍스처를 읽어 r = 1 + 과장 배율 · max(고도, 해수면) / 반지름 으로 밀어 냅니다. 면 경계의 꼭짓점은
## 이웃 면과 비트 단위로 같은 방향이라 틈이 없습니다.

## 고른 필드가 바뀌었을 때.
signal field_changed(name: String)
## 고도 과장, 겹쳐 보기, 경위선, 그늘이 바뀌었을 때.
signal display_changed

const SHADER := preload("res://shaders/globe.gdshader")
const ATMOSPHERE_SHADER := preload("res://shaders/atmosphere.gdshader")
const DEFAULT_EXAGGERATION := 20.0
const MIN_EXAGGERATION := 1.0
const MAX_EXAGGERATION := 100.0
## [ ] 키와 패널 버튼이 오르내리는 고도 과장 단계.
const EXAGGERATION_STEPS: Array[float] = [1.0, 2.0, 5.0, 10.0, 20.0, 30.0, 50.0, 75.0, 100.0]
## 겹쳐 보기 이름 (globe.json 의 overlays[].name).
const OVERLAY_RIVERS := "rivers"
const OVERLAY_LAKES := "lakes"
## 강 유량 등급 (globe.json 의 rivers 값 1~4). 기본은 아주 큰 강(4 등급, 1000 m³/s 이상)만
## 그립니다. L0 칸(약 20 km)에서는 육지 대부분이 강 문턱을 넘어 3 등급부터 그리면 지형을 가립니다.
## L0 칸이 커서 육지 대부분이 강 문턱을 넘기 때문입니다 (bpcg.bake.globe 의 rivers 설명).
const DEFAULT_RIVER_MIN_CLASS := 4
const MAX_RIVER_CLASS := 7
## globe.json 에 색이 없을 때 쓰는 강·호수 색 (sRGB).
const RIVER_FALLBACK_COLOR := Color8(30, 100, 210)
const LAKE_FALLBACK_COLOR := Color8(90, 200, 250)
## 경위선 간격 (도).
const GRID_STEP_DEG := 15.0
## 대기 빛 구의 반지름 (지구본 반지름 배).
const ATMOSPHERE_RADIUS := 1.06
## 히어로 표시 핀의 크기 (지구본 반지름 배).
const PIN_HEIGHT := 0.06
const PIN_RADIUS := 0.0035
const PIN_HEAD_RADIUS := 0.012
const PIN_COLOR := Color(1.0, 0.36, 0.14)
## 화면에서 고르기: 해수면 구와 만난 뒤 그 자리 높이의 구로 다시 맞히는 횟수.
const PICK_ITERATIONS := 4
## 드문 범주 칸의 점: 화면 크기(픽셀)와 지표 위로 띄우는 높이(지구본 반지름 배), 점 수 상한.
const MARKER_POINT_SIZE_PX := 7.0
const MARKER_LIFT := 0.002
const MAX_POINT_MARKERS := 20000

var data: GlobeData
## 지금 칠한 필드 이름.
var active_field := ""
## 고도 과장 배율 (1 = 실제 비율).
var exaggeration := DEFAULT_EXAGGERATION
var show_rivers := true
var show_lakes := true
var show_grid := true
## 기복 그늘과 대기 빛 (꾸밈, 자료 아님). 끄면 화면 색이 범례 색과 같습니다.
var show_shading := true
## 이 등급 이상의 강만 그립니다 (1 = 모든 강 칸).
var river_min_class := DEFAULT_RIVER_MIN_CLASS
## 면 메시 6개 (면 번호 순서)와 그 재질.
var faces: Array[MeshInstance3D] = []
var materials: Array[ShaderMaterial] = []
## 히어로 유역 표시 (없으면 null).
var hero_marker: Node3D
var hero_label: Label3D
var atmosphere: MeshInstance3D
## 드문 범주 칸의 점 (point_markers 필드를 골랐을 때만, 없으면 null)과 그 수.
var point_markers: MeshInstance3D
var marker_count := 0


## 자료로 지구본을 만듭니다. 자료가 없거나 잘못됐으면 아무것도 그리지 않습니다.
func setup(globe_data: GlobeData) -> void:
	clear()
	data = globe_data
	if data == null or not data.is_valid():
		return
	var corners := data.corner_textures()
	var overlay := data.overlay_textures()
	var box := _cull_box()
	for f in GlobeData.FACE_COUNT:
		var m := ShaderMaterial.new()
		m.shader = SHADER
		m.set_shader_parameter("corner_elevation", corners[f])
		m.set_shader_parameter("overlay", overlay[f])
		m.set_shader_parameter("face_res", data.face_res)
		m.set_shader_parameter("radius_m", data.radius_m)
		m.set_shader_parameter("sea_level_m", data.sea_level_m)
		m.set_shader_parameter("grid_step_deg", GRID_STEP_DEG)
		m.set_shader_parameter("river_colors", _river_colors())
		m.set_shader_parameter("lake_color", data.overlay_value_color(
				OVERLAY_LAKES, 1, LAKE_FALLBACK_COLOR))
		materials.append(m)
		var mi := MeshInstance3D.new()
		mi.name = "Face%d" % f
		mi.mesh = build_face_mesh(f)
		mi.material_override = m
		# 셰이더가 꼭짓점을 밀어 내므로 화면 밖 판정 상자를 가장 큰 과장에 맞춥니다.
		mi.custom_aabb = box
		mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		add_child(mi)
		faces.append(mi)
	_build_atmosphere()
	_build_hero_marker()
	_apply_display()
	set_field(data.default_field())


## 만든 자식을 모두 지웁니다.
func clear() -> void:
	for child in get_children():
		remove_child(child)
		child.queue_free()
	faces.clear()
	materials.clear()
	hero_marker = null
	hero_label = null
	atmosphere = null
	point_markers = null
	marker_count = 0
	active_field = ""


func is_ready() -> bool:
	return data != null and data.is_valid() and faces.size() == GlobeData.FACE_COUNT


## 면 f 의 메시: 모서리 (N+1)² 개의 단위 방향, UV = (열/N, 행/N), 바깥이 앞면.
func build_face_mesh(face: int) -> ArrayMesh:
	var n := data.face_res
	var side := n + 1
	# 모서리 좌표의 tan 값. 가운데를 기준으로 거울 대칭이 정확하도록 반만 계산합니다.
	var tans := PackedFloat64Array()
	tans.resize(side)
	for k in range(0, floori(n / 2.0) + 1):
		var t := GlobeData.tan_quarter(-1.0 + 2.0 * float(k) / float(n))
		tans[k] = t
		tans[n - k] = -t
	var nf := data.face_n[face]
	var uf := data.face_u[face]
	var vf := data.face_v[face]
	var vertices := PackedVector3Array()
	vertices.resize(side * side)
	var normals := PackedVector3Array()
	normals.resize(side * side)
	var uvs := PackedVector2Array()
	uvs.resize(side * side)
	for r in side:
		for c in side:
			var i := r * side + c
			var d := (nf + uf * tans[c] + vf * tans[r]).normalized()
			vertices[i] = d
			normals[i] = d
			uvs[i] = Vector2(float(c) / float(n), float(r) / float(n))

	# Godot 는 시계 방향(바깥에서 볼 때)을 앞면으로 봅니다. u × v = n 이므로
	# (북서, 남서, 북동) 순서가 바깥에서 시계 방향입니다 (heightmap_loader.gd 와 반대 축이라 순서도 반대).
	var indices := PackedInt32Array()
	indices.resize(n * n * 6)
	var k := 0
	for r in n:
		for c in n:
			var nw := r * side + c
			var ne := nw + 1
			var sw := nw + side
			var se := sw + 1
			indices[k] = nw
			indices[k + 1] = sw
			indices[k + 2] = ne
			indices[k + 3] = ne
			indices[k + 4] = sw
			indices[k + 5] = se
			k += 6

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


## 필드를 고릅니다. 텍스처는 처음 고를 때 만듭니다. 없는 이름이면 false.
func set_field(name: String) -> bool:
	if not is_ready() or not data.has_field(name):
		return false
	var textures := data.field_textures(name)
	var categorical := data.is_categorical(name)
	for f in materials.size():
		materials[f].set_shader_parameter("field_color", textures[f])
		materials[f].set_shader_parameter("categorical", categorical)
	active_field = name
	_update_point_markers()
	field_changed.emit(name)
	return true


## 고도 과장 배율 (1 ~ 100).
func set_exaggeration(value: float) -> void:
	exaggeration = clampf(value, MIN_EXAGGERATION, MAX_EXAGGERATION)
	for m in materials:
		m.set_shader_parameter("exaggeration", exaggeration)
	_place_hero_marker()
	_update_point_markers()
	display_changed.emit()


## 고도 과장을 한 단계 올리거나(step > 0) 내립니다.
func step_exaggeration(step: int) -> void:
	if step > 0:
		for s in EXAGGERATION_STEPS:
			if s > exaggeration + 1e-6:
				set_exaggeration(s)
				return
		set_exaggeration(MAX_EXAGGERATION)
	elif step < 0:
		for i in range(EXAGGERATION_STEPS.size() - 1, -1, -1):
			if EXAGGERATION_STEPS[i] < exaggeration - 1e-6:
				set_exaggeration(EXAGGERATION_STEPS[i])
				return
		set_exaggeration(MIN_EXAGGERATION)


func has_overlay(name: String) -> bool:
	return data != null and data.has_overlay(name)


func is_overlay_visible(name: String) -> bool:
	match name:
		OVERLAY_RIVERS:
			return show_rivers
		OVERLAY_LAKES:
			return show_lakes
	return false


func set_overlay_visible(name: String, on: bool) -> void:
	match name:
		OVERLAY_RIVERS:
			show_rivers = on
		OVERLAY_LAKES:
			show_lakes = on
		_:
			return
	_apply_display()
	display_changed.emit()


func toggle_overlay(name: String) -> void:
	set_overlay_visible(name, not is_overlay_visible(name))


## 겹쳐 보기 값 하나가 지금 화면에 그려지지 않는 까닭 (그려지면 빈 글). 마우스 자리 읽기가 씁니다.
func overlay_hidden_reason(name: String, value: int) -> String:
	if value <= 0:
		return "값 없음"
	if not is_overlay_visible(name):
		return "꺼 둠"
	if name == OVERLAY_RIVERS and value < river_min_class:
		return "지금은 %d 등급부터 그리므로 그리지 않음" % river_min_class
	return ""


## 그릴 강의 가장 낮은 유량 등급 (1 ~ 가장 높은 등급).
func set_river_min_class(value: int) -> void:
	river_min_class = clampi(value, 1, max_river_class())
	_apply_display()
	display_changed.emit()


## 강 등급 문턱을 한 단계 올립니다. 가장 높은 등급 다음은 1 등급(모든 강)입니다.
func cycle_river_min_class() -> void:
	set_river_min_class(1 if river_min_class >= max_river_class() else river_min_class + 1)


## globe.json 의 강 등급 가운데 가장 높은 것 (없으면 1).
func max_river_class() -> int:
	var top := 1
	if data != null:
		for cat in data.overlay(OVERLAY_RIVERS).get("categories", []):
			if cat is Dictionary:
				top = maxi(top, int(cat.get("id", 0)))
	return mini(top, MAX_RIVER_CLASS)


func set_grid_visible(on: bool) -> void:
	show_grid = on
	_apply_display()
	display_changed.emit()


func toggle_grid() -> void:
	set_grid_visible(not show_grid)


## 기복 그늘과 대기 빛(꾸밈)을 켜거나 끕니다. 끄면 모든 칸이 범례 색 그대로입니다.
func set_shading_visible(on: bool) -> void:
	show_shading = on
	_apply_display()
	display_changed.emit()


func toggle_shading() -> void:
	set_shading_visible(not show_shading)


## 방향의 지표 반지름 (지구본 반지름 배). 모서리 고도를 쌍선형 보간해 메시와 거의 같습니다.
func surface_radius(dir: Vector3) -> float:
	if not is_ready():
		return 1.0
	var z := maxf(data.corner_elevation_at(dir), data.sea_level_m)
	return 1.0 + exaggeration * z / data.radius_m


## 화면 위치가 가리키는 지구본 위 방향 (지구본 좌표의 단위 벡터). 지구본을 맞히지 않으면 ZERO.
func pick(camera: Camera3D, screen_pos: Vector2) -> Vector3:
	if not is_ready() or camera == null:
		return Vector3.ZERO
	var origin := to_local(camera.project_ray_origin(screen_pos))
	var ray := (global_basis.inverse() * camera.project_ray_normal(screen_pos)).normalized()
	var radius := 1.0
	var hit := Vector3.ZERO
	for i in PICK_ITERATIONS:
		var t := _ray_sphere(origin, ray, radius)
		if t < 0.0:
			return hit
		hit = (origin + ray * t).normalized()
		radius = surface_radius(hit)
	return hit


## 히어로 유역 방향 (없으면 ZERO).
func hero_direction() -> Vector3:
	return Vector3.ZERO if data == null else data.hero_direction()


func _apply_display() -> void:
	for m in materials:
		m.set_shader_parameter("exaggeration", exaggeration)
		m.set_shader_parameter("show_rivers", show_rivers)
		m.set_shader_parameter("show_lakes", show_lakes)
		m.set_shader_parameter("show_grid", show_grid)
		m.set_shader_parameter("river_min_class", river_min_class)
		m.set_shader_parameter("shade_relief", show_shading)
	if atmosphere != null:
		atmosphere.visible = show_shading


## 강 등급별 색 (셰이더용 선형 색, 0 ~ MAX_RIVER_CLASS 번).
func _river_colors() -> PackedColorArray:
	var colors := PackedColorArray()
	for k in MAX_RIVER_CLASS + 1:
		var c := data.overlay_value_color(OVERLAY_RIVERS, k, RIVER_FALLBACK_COLOR)
		colors.append(c.srgb_to_linear())
	return colors


func _cull_box() -> AABB:
	var top_m := maxf(data.corner_max_m, data.sea_level_m)
	var r := 1.0 + MAX_EXAGGERATION * maxf(top_m, 0.0) / data.radius_m + PIN_HEIGHT + 0.05
	return AABB(Vector3(-r, -r, -r), Vector3(2.0 * r, 2.0 * r, 2.0 * r))


func _build_atmosphere() -> void:
	var sphere := SphereMesh.new()
	sphere.radius = ATMOSPHERE_RADIUS
	sphere.height = 2.0 * ATMOSPHERE_RADIUS
	sphere.radial_segments = 96
	sphere.rings = 48
	var m := ShaderMaterial.new()
	m.shader = ATMOSPHERE_SHADER
	m.set_shader_parameter("globe_radius", 1.0)
	m.set_shader_parameter("glow_radius", ATMOSPHERE_RADIUS)
	atmosphere = MeshInstance3D.new()
	atmosphere.name = "Atmosphere"
	atmosphere.mesh = sphere
	atmosphere.material_override = m
	atmosphere.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(atmosphere)


func _build_hero_marker() -> void:
	var dir := data.hero_direction()
	if dir == Vector3.ZERO:
		return
	hero_marker = Node3D.new()
	hero_marker.name = "Hero"
	var pin_material := StandardMaterial3D.new()
	pin_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	pin_material.albedo_color = PIN_COLOR
	var stick_mesh := CylinderMesh.new()
	stick_mesh.top_radius = PIN_RADIUS
	stick_mesh.bottom_radius = PIN_RADIUS
	stick_mesh.height = PIN_HEIGHT
	stick_mesh.radial_segments = 12
	stick_mesh.rings = 1
	var stick := MeshInstance3D.new()
	stick.name = "Pin"
	stick.mesh = stick_mesh
	stick.material_override = pin_material
	stick.position = Vector3(0.0, PIN_HEIGHT * 0.5, 0.0)
	hero_marker.add_child(stick)
	var head_mesh := SphereMesh.new()
	head_mesh.radius = PIN_HEAD_RADIUS
	head_mesh.height = 2.0 * PIN_HEAD_RADIUS
	head_mesh.radial_segments = 16
	head_mesh.rings = 8
	var head := MeshInstance3D.new()
	head.name = "Head"
	head.mesh = head_mesh
	head.material_override = pin_material
	head.position = Vector3(0.0, PIN_HEIGHT, 0.0)
	hero_marker.add_child(head)

	var font := SystemFont.new()
	font.font_names = PackedStringArray(GlobeHud.FONT_NAMES)
	hero_label = Label3D.new()
	hero_label.name = "Label"
	hero_label.font = font
	hero_label.font_size = 30
	hero_label.outline_size = 10
	hero_label.pixel_size = 0.0009
	hero_label.fixed_size = true
	hero_label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	hero_label.vertical_alignment = VERTICAL_ALIGNMENT_BOTTOM
	hero_label.position = Vector3(0.0, PIN_HEIGHT + PIN_HEAD_RADIUS * 1.5, 0.0)
	var title := str(data.hero.get("label", "히어로 유역"))
	hero_label.text = "%s (%s)" % [title, GlobeData.format_lat_lon(GlobeData.lat_lon(dir))]
	hero_marker.add_child(hero_label)
	add_child(hero_marker)
	_place_hero_marker()


## 고른 필드가 point_markers 이면 0 이 아닌 칸마다 범주 색 점을 찍습니다 (화면 크기 일정).
## 칸이 작아 화면에서 놓치기 쉬운 드문 칸(동굴 등)을 보이게 하려는 것이고, 점 크기는 넓이와
## 상관없습니다. 점은 칸 모서리 가운데 가장 높은 곳보다 조금 위에 둡니다(메시에 묻히지 않게).
func _update_point_markers() -> void:
	if point_markers != null:
		remove_child(point_markers)
		point_markers.queue_free()
		point_markers = null
	marker_count = 0
	if not is_ready() or not data.has_point_markers(active_field):
		return
	var cells := data.marker_cells(active_field)
	if cells.is_empty() or cells.size() > MAX_POINT_MARKERS:
		return
	var values: PackedByteArray = data.values(active_field)
	var nn := data.face_res * data.face_res
	var points := PackedVector3Array()
	var colors := PackedColorArray()
	for index in cells:
		var face := floori(float(index) / float(nn))
		var rest := index - face * nn
		var row := floori(float(rest) / float(data.face_res))
		var col := rest - row * data.face_res
		var dir := data.cell_dir(face, row, col)
		var z := maxf(data.cell_top_elevation(face, row, col), data.sea_level_m)
		points.append(dir * (1.0 + exaggeration * z / data.radius_m + MARKER_LIFT))
		colors.append(data.category_color(active_field, values[index]))
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = points
	arrays[Mesh.ARRAY_COLOR] = colors
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_POINTS, arrays)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.vertex_color_use_as_albedo = true
	material.vertex_color_is_srgb = true
	material.use_point_size = true
	material.point_size = MARKER_POINT_SIZE_PX
	point_markers = MeshInstance3D.new()
	point_markers.name = "Markers"
	point_markers.mesh = mesh
	point_markers.material_override = material
	point_markers.custom_aabb = _cull_box()
	point_markers.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(point_markers)
	marker_count = points.size()


## 핀을 히어로 방향의 지표에 세웁니다 (핀의 +Y 가 바깥).
func _place_hero_marker() -> void:
	if hero_marker == null:
		return
	var dir := data.hero_direction()
	var basis := Basis.IDENTITY
	if dir.dot(Vector3.UP) < -0.9999:
		basis = Basis(Vector3.RIGHT, PI)
	elif dir.dot(Vector3.UP) < 0.9999:
		basis = Basis(Quaternion(Vector3.UP, dir))
	hero_marker.transform = Transform3D(basis, dir * surface_radius(dir))


## 원점 중심 반지름 radius 구와 광선이 처음 만나는 거리. 안 만나면 -1.
static func _ray_sphere(origin: Vector3, ray: Vector3, radius: float) -> float:
	var b := origin.dot(ray)
	var c := origin.length_squared() - radius * radius
	var disc := b * b - c
	if disc < 0.0:
		return -1.0
	var root := sqrt(disc)
	var t := -b - root
	if t < 0.0:
		t = -b + root
	return t if t >= 0.0 else -1.0
