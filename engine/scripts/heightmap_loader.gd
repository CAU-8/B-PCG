class_name HeightmapLoader
extends RefCounted
## Python(bpcg.bake.heightmap)이 구운 높이맵 한 장(.bin + .json)을 읽습니다.
##
## 읽은 뒤 그리기용 [ArrayMesh] 와 충돌용 [HeightMapShape3D] 를 만듭니다.
## [br][br]
## 좌표 약속 (Godot 국소 접평면 좌표, 단위 m):[br]
## - X = 동쪽, Y = 위, Z = 남쪽.[br]
## - 열(col)이 늘면 +X, 행(row)이 늘면 +Z. 0번 행이 북쪽 끝입니다.[br]
## - 표본 (row, col) 의 위치 = origin + (col * spacing_m, 높이, row * spacing_m).[br]
## .bin 은 float32 리틀 엔디언입니다. Godot 가 도는 기기(x86_64, arm64)는 모두 리틀 엔디언이라
## 바이트를 그대로 float 배열로 바꿉니다.

const FORMAT := "float32_le"

## 열 수 (X 방향 표본 수).
var width: int = 0
## 행 수 (Z 방향 표본 수). 고도가 아닙니다.
var height: int = 0
## 표본 사이 수평 간격 (m).
var spacing_m: float = 0.0
## 북서쪽 모서리 표본(0번 행, 0번 열)의 국소 좌표 (m).
var origin := Vector3.ZERO
## .json 에 적힌 최저, 최고 높이 (m).
var min_height_m: float = 0.0
var max_height_m: float = 0.0
## 높이 표본 (m), 행 우선. 인덱스 = row * width + col.
var heights := PackedFloat32Array()
## .json 내용 전체.
var meta: Dictionary = {}
## 실패 이유. 비어 있으면 읽기에 성공한 것입니다.
var error_message: String = ""
## 읽은 파일의 경로 (확장자 뺀 것).
var stem: String = ""


## stem.json 과 stem.bin 이 둘 다 있는지 봅니다.
static func exists(path_stem: String) -> bool:
	return (FileAccess.file_exists(path_stem + ".json")
			and FileAccess.file_exists(path_stem + ".bin"))


## 높이맵을 읽습니다. 실패해도 null 이 아니라 error_message 가 채워진 객체를 돌려줍니다.
static func load_stem(path_stem: String) -> HeightmapLoader:
	var loader := HeightmapLoader.new()
	loader._read(path_stem)
	return loader


func is_valid() -> bool:
	return error_message.is_empty()


## 그리기용 메시를 만듭니다. 꼭짓점은 origin 을 뺀 타일 국소 좌표이므로
## MeshInstance3D 를 origin 에 놓아야 합니다.
## [br]법선은 중심 차분으로 직접 계산합니다 (SurfaceTool.generate_normals 보다 훨씬 빠름).
## [br]skirt_m > 0 이면 가장자리를 따라 그만큼 아래로 내려가는 벽(양면)을 덧붙입니다.
## 옆 타일과 높이가 조금 달라 생기는 틈을 가립니다. 꼭짓점은 width * height 개 뒤에 붙습니다.
func build_mesh(skirt_m: float = 0.0) -> ArrayMesh:
	var count := width * height
	var ring := _perimeter() if skirt_m > 0.0 else PackedInt32Array()
	var vertices := PackedVector3Array()
	vertices.resize(count + ring.size())
	var normals := PackedVector3Array()
	normals.resize(count + ring.size())
	var uvs := PackedVector2Array()
	uvs.resize(count + ring.size())

	for row in height:
		var row_n := maxi(row - 1, 0)
		var row_s := mini(row + 1, height - 1)
		var dz_m := float(row_s - row_n) * spacing_m
		var base := row * width
		for col in width:
			var col_w := maxi(col - 1, 0)
			var col_e := mini(col + 1, width - 1)
			var i := base + col
			vertices[i] = Vector3(float(col) * spacing_m, heights[i], float(row) * spacing_m)
			var dx_m := float(col_e - col_w) * spacing_m
			var dh_dx := (heights[base + col_e] - heights[base + col_w]) / dx_m
			var dh_dz := (heights[row_s * width + col] - heights[row_n * width + col]) / dz_m
			normals[i] = Vector3(-dh_dx, 1.0, -dh_dz).normalized()
			uvs[i] = Vector2(float(col) / float(width - 1), float(row) / float(height - 1))

	# Godot 는 시계 방향(위에서 볼 때)을 앞면으로 봅니다.
	var indices := PackedInt32Array()
	indices.resize((width - 1) * (height - 1) * 6 + ring.size() * 12)
	var k := 0
	for row in height - 1:
		for col in width - 1:
			var nw := row * width + col
			var ne := nw + 1
			var sw := nw + width
			var se := sw + 1
			indices[k] = nw
			indices[k + 1] = ne
			indices[k + 2] = sw
			indices[k + 3] = ne
			indices[k + 4] = se
			indices[k + 5] = sw
			k += 6

	# 치마(skirt): 둘레 표본마다 skirt_m 아래 꼭짓점을 하나 두고, 이웃 둘레 표본과 사각형을 만듭니다.
	# 양쪽에서 보이도록 두 감김 방향을 모두 넣습니다.
	for j in ring.size():
		var top := ring[j]
		var v := count + j
		vertices[v] = vertices[top] - Vector3(0.0, skirt_m, 0.0)
		normals[v] = normals[top]
		uvs[v] = uvs[top]
	for j in ring.size():
		var a := ring[j]
		var b := ring[(j + 1) % ring.size()]
		var a_low := count + j
		var b_low := count + (j + 1) % ring.size()
		for tri in [[a, b, a_low], [b, b_low, a_low], [a, a_low, b], [b, a_low, b_low]]:
			indices[k] = tri[0]
			indices[k + 1] = tri[1]
			indices[k + 2] = tri[2]
			k += 3

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


## 충돌 모양을 만듭니다. 놓을 자리는 shape_transform() 으로 얻습니다.
## [br]hole 의 넓이가 0 보다 크면 그 사각형(국소 X, Z) 안 표본을 최저 높이보다 hole_drop_m 아래로
## 내립니다. 더 고운 지형이 그 자리를 맡을 때 두 충돌면이 겹치지 않게 합니다.
func build_shape(hole := Rect2(), hole_drop_m: float = 50.0) -> HeightMapShape3D:
	var shape := HeightMapShape3D.new()
	shape.map_width = width
	shape.map_depth = height
	if hole.has_area():
		var data := heights.duplicate()
		var low := min_height_m - hole_drop_m
		# 사각형 안쪽(경계 제외)에 드는 행·열 범위
		var col_lo := maxi(floori((hole.position.x - origin.x) / spacing_m) + 1, 0)
		var col_hi := mini(ceili((hole.end.x - origin.x) / spacing_m) - 1, width - 1)
		var row_lo := maxi(floori((hole.position.y - origin.z) / spacing_m) + 1, 0)
		var row_hi := mini(ceili((hole.end.y - origin.z) / spacing_m) - 1, height - 1)
		for row in range(row_lo, row_hi + 1):
			for col in range(col_lo, col_hi + 1):
				data[row * width + col] = low
		shape.map_data = data
	else:
		shape.map_data = heights
	return shape


## HeightMapShape3D 는 가운데가 원점이고 표본 간격이 1 이므로,
## 수평으로 spacing_m 배 늘이고 타일 가운데로 옮기는 변환을 돌려줍니다.
func shape_transform() -> Transform3D:
	var center := origin + Vector3(
		float(width - 1) * spacing_m * 0.5, 0.0, float(height - 1) * spacing_m * 0.5)
	return Transform3D(Basis.from_scale(Vector3(spacing_m, 1.0, spacing_m)), center)


## 타일 가운데의 국소 좌표 (y 는 그 자리 높이, m).
func center() -> Vector3:
	var p := origin + Vector3(
		float(width - 1) * spacing_m * 0.5, 0.0, float(height - 1) * spacing_m * 0.5)
	p.y = height_at(p)
	return p


## 국소 좌표 (x, z) 의 높이를 쌍선형 보간으로 구합니다 (m). 타일 밖이면 NAN.
func height_at(local_pos: Vector3) -> float:
	if not is_valid():
		return NAN
	var fx := (local_pos.x - origin.x) / spacing_m
	var fz := (local_pos.z - origin.z) / spacing_m
	if fx < 0.0 or fz < 0.0 or fx > float(width - 1) or fz > float(height - 1):
		return NAN
	var col := mini(int(fx), width - 2)
	var row := mini(int(fz), height - 2)
	var tx := fx - float(col)
	var tz := fz - float(row)
	var i := row * width + col
	var north := lerpf(heights[i], heights[i + 1], tx)
	var south := lerpf(heights[i + width], heights[i + width + 1], tx)
	return origin.y + lerpf(north, south, tz)


## 둘레 표본 번호 (시계 방향, 겹침 없음): 북쪽 행 → 동쪽 열 → 남쪽 행 → 서쪽 열.
func _perimeter() -> PackedInt32Array:
	var ring := PackedInt32Array()
	for col in width:
		ring.append(col)
	for row in range(1, height):
		ring.append(row * width + width - 1)
	for col in range(width - 2, -1, -1):
		ring.append((height - 1) * width + col)
	for row in range(height - 2, 0, -1):
		ring.append(row * width)
	return ring


func _read(path_stem: String) -> void:
	stem = path_stem
	var json_path := path_stem + ".json"
	var bin_path := path_stem + ".bin"
	if not FileAccess.file_exists(json_path):
		error_message = "설명 파일이 없습니다: %s" % json_path
		return
	if not FileAccess.file_exists(bin_path):
		error_message = "높이 파일이 없습니다: %s" % bin_path
		return

	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(json_path))
	if not (parsed is Dictionary):
		error_message = "설명 파일을 JSON 으로 읽지 못했습니다: %s" % json_path
		return
	meta = parsed
	for key in ["format", "width", "height", "spacing_m", "min", "max", "origin"]:
		if not meta.has(key):
			error_message = "%s 에 '%s' 필드가 없습니다" % [json_path, key]
			return
	if meta["format"] != FORMAT:
		error_message = "지원하지 않는 형식입니다: %s (읽을 수 있는 형식: %s)" % [meta["format"], FORMAT]
		return

	width = int(meta["width"])
	height = int(meta["height"])
	spacing_m = float(meta["spacing_m"])
	min_height_m = float(meta["min"])
	max_height_m = float(meta["max"])
	var o: Array = meta["origin"]
	if width < 2 or height < 2 or spacing_m <= 0.0 or o.size() != 3:
		error_message = "%s 의 크기, 간격, 원점 값이 잘못됐습니다" % json_path
		return
	origin = Vector3(float(o[0]), float(o[1]), float(o[2]))

	var bytes := FileAccess.get_file_as_bytes(bin_path)
	if bytes.size() != width * height * 4:
		error_message = "%s 크기 %d 바이트가 width * height * 4 = %d 와 다릅니다" % [
			bin_path, bytes.size(), width * height * 4]
		return
	heights = bytes.to_float32_array()
