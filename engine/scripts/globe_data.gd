class_name GlobeData
extends RefCounted
## 지구본 자료(<굽기 폴더>/globe/globe.json 과 .bin 파일)를 읽습니다. 파이썬 bpcg.bake.globe 가 씁니다.
##
## 좌표 약속 (globe.json 의 frame):[br]
## - Godot 좌표 = (행성 x, 행성 z, -행성 y). 행성 자전축 z(북쪽)가 Godot +Y 입니다. 설정의 자전축이
##   z 가 아니면 '행성' 대신 frame.axis_rotation 으로 돌린 좌표이고, 자전축은 늘 Godot +Y 입니다.[br]
## - 반대로 행성 (x, y, z) = (gx, -gz, gy). 위도 = asin(gy), 경도 = atan2(-gz, gx).[br]
## - 방향은 모두 단위 구 위의 단위 벡터이고, 반지름 1 이 해수면 반지름 radius_m 입니다.
## [br][br]
## 격자 약속 (globe.json 의 mapping, 등각 큐브스피어):[br]
## - 면 f 의 기저 n, u, v (Godot 좌표). 방향 ∝ n + tan(a·π/4)·u + tan(b·π/4)·v, a, b ∈ [-1, 1].[br]
## - 칸 (row r, col c) 중심: a = -1 + 2(c + 0.5)/N, b = -1 + 2(r + 0.5)/N. 열이 u, 행이 v 쪽입니다.[br]
## - 모서리 (r, c): a = -1 + 2c/N, b = -1 + 2r/N. 모서리는 (N+1)² 개입니다.[br]
## - 배열 순서는 [면, 행, 열] 이고 인덱스 = f·N² + r·N + c (모서리는 f·(N+1)² + r·(N+1) + c).[br]
## .bin 은 머리글 없는 리틀 엔디언 float32 또는 uint8 입니다. NaN 은 '값 없음' 입니다.
## 범주 필드는 categories 에 no_data: true 인 범주(보통 255, 바다의 지질)가 '값 없음' 입니다.
## [br][br]
## 필드 텍스처의 알파는 투명도가 아니라 텍셀의 '종류'입니다(셰이더가 보간할지 정함):
## TEXEL_VALUE(255) = 값, TEXEL_BELOW_BREAK(128) = 색이 끊기는 값(color_break) 아래, TEXEL_NO_DATA(0) = 값
## 없음. 종류가 다른 텍셀끼리는 섞지 않아 범례에 없는 색이 생기지 않습니다.

const FORMAT := "bpcg-globe"
const FORMAT_VERSION := 1
## 굽기 폴더 안의 지구본 폴더 이름과 설명 파일 이름.
const SUBDIR := "globe"
const MANIFEST := "globe.json"
## 고도 필드 이름. 마우스 자리 읽기에서 늘 함께 보여 줍니다.
const ELEVATION_FIELD := "elevation"
## 값이 없는 칸(NaN)의 색과 글.
const NO_DATA_COLOR := Color8(128, 128, 128)
const NO_DATA_TEXT := "값 없음"
const FACE_COUNT := 6
const QUARTER_PI := PI / 4.0
## 필드 텍스처 알파 = 텍셀의 종류 (셰이더 globe.gdshader 의 field_rgb 가 읽음).
const TEXEL_VALUE := 255
const TEXEL_BELOW_BREAK := 128
const TEXEL_NO_DATA := 0
## 색 지도에서 구간 시작 위치를 빨리 찾으려고 값 범위를 나누는 칸 수.
const _LUT_BINS := 1024

## 읽은 폴더 (끝에 / 없음).
var folder := ""
## globe.json 내용 전체.
var meta: Dictionary = {}
## 실패 이유. 비어 있으면 읽기에 성공한 것입니다.
var error_message := ""
## 실패했을 때 자료가 아예 없었는지(globe.json 이 없음, true) 아니면 있는데 잘못됐는지(false).
var missing := false
## 면 한 변의 칸 수 N.
var face_res := 0
## 해수면 반지름 (m). 지구본에서는 1 로 줄여 그립니다.
var radius_m := 6371000.0
## 해수면 고도 (m). 이보다 낮은 곳(바다)은 해수면 높이로 그립니다.
var sea_level_m := 0.0
## 면 기저 (Godot 좌표, 면 번호 순서).
var face_n: Array[Vector3] = []
var face_u: Array[Vector3] = []
var face_v: Array[Vector3] = []
## 필드 설명 (globe.json 의 fields 순서).
var fields: Array[Dictionary] = []
## 겹쳐 보기 설명 (강, 호수).
var overlays: Array[Dictionary] = []
## 히어로 유역 (globe.json 의 hero). 없으면 빈 사전.
var hero: Dictionary = {}
## 값이 없는 칸의 색 (globe.json 의 nan_rgb, 없으면 NO_DATA_COLOR).
var no_data_color := NO_DATA_COLOR
## 모서리 고도 (m), 6·(N+1)² 개. NaN 은 해수면으로 바꿔 둡니다.
var corner_elevation_m := PackedFloat32Array()
var corner_min_m := 0.0
var corner_max_m := 0.0

var _field_by_name := {}
## 필드 이름 → PackedFloat32Array (float32) 또는 PackedByteArray (uint8), 6·N² 개.
var _values := {}
## 겹쳐 보기 이름 → PackedByteArray, 6·N² 개.
var _overlay_values := {}
## 필드 이름 → Array[ImageTexture] (면 6장). 처음 고를 때 만듭니다.
var _textures := {}
var _corner_textures: Array[ImageTexture] = []
var _overlay_textures: Array[ImageTexture] = []
## 필드 이름 → {"values": PackedFloat64Array, "colors": PackedColorArray, "starts": ...}
var _colormaps := {}
## 필드 이름 → {번호: {"label", "color", "no_data"}}
var _categories := {}
## 필드 이름 → PackedInt32Array (점으로 찍을 칸 번호, point_markers 필드만)
var _marker_cells := {}


## 기본 지구본 폴더: <BakedPaths.dir()>/globe.
static func default_dir() -> String:
	return BakedPaths.dir().path_join(SUBDIR)


## 지구본 자료를 읽습니다. 실패해도 null 이 아니라 error_message 가 채워진 객체를 돌려줍니다.
static func load_dir(dir: String = "") -> GlobeData:
	var data := GlobeData.new()
	data._read(default_dir() if dir.is_empty() else dir.trim_suffix("/"))
	return data


## 위도·경도 (도). x = 위도(북 +), y = 경도(동 +, -180 ~ 180).
static func lat_lon(dir: Vector3) -> Vector2:
	var d := dir.normalized()
	return Vector2(rad_to_deg(asin(clampf(d.y, -1.0, 1.0))), rad_to_deg(atan2(-d.z, d.x)))


## 위도·경도 (도) → Godot 좌표의 단위 방향.
static func direction_from_lat_lon(lat_deg: float, lon_deg: float) -> Vector3:
	var lat := deg_to_rad(lat_deg)
	var lon := deg_to_rad(lon_deg)
	# 행성 (cos·cos, cos·sin, sin) → Godot (px, pz, -py)
	return Vector3(cos(lat) * cos(lon), sin(lat), -cos(lat) * sin(lon))


## "북위 37.52°, 동경 127.03°" 꼴의 글.
static func format_lat_lon(ll: Vector2, digits: int = 2) -> String:
	var fmt := "%." + str(digits) + "f°"
	var ns := "북위 " if ll.x >= 0.0 else "남위 "
	var ew := "동경 " if ll.y >= 0.0 else "서경 "
	return ns + fmt % absf(ll.x) + ", " + ew + fmt % absf(ll.y)


## 사람이 읽기 좋은 수. 1000 이상은 세 자리마다 쉼표, 아주 작은 수는 a×10ⁿ.
static func format_number(v: float) -> String:
	if is_nan(v):
		return NO_DATA_TEXT
	if is_inf(v):
		return "∞" if v > 0.0 else "-∞"
	var a := absf(v)
	if a >= 1.0e9:
		return _scientific(v)
	if a >= 1000.0:
		return _group_thousands(roundi(v))
	if a >= 100.0:
		return "%.0f" % v
	if a >= 10.0:
		return "%.1f" % v
	if a >= 1.0:
		return "%.2f" % v
	if a == 0.0:
		return "0"
	if a >= 0.01:
		return "%.3f" % v
	return _scientific(v)


func is_valid() -> bool:
	return error_message.is_empty()


# ---------------------------------------------------------------- 필드

func has_field(name: String) -> bool:
	return _field_by_name.has(name)


## 필드 설명 사전 (없으면 빈 사전).
func field(name: String) -> Dictionary:
	return _field_by_name.get(name, {})


## globe.json 순서의 필드 이름.
func field_names() -> PackedStringArray:
	var names := PackedStringArray()
	for f in fields:
		names.append(f["name"])
	return names


## 무리(group)별로 모은 필드 이름. 무리 순서는 globe.json 에 처음 나온 순서입니다.
## 패널의 순서와 숫자 키(1~9)가 이 순서를 따릅니다.
func ordered_field_names() -> PackedStringArray:
	var groups: Array[String] = []
	for f in fields:
		var g := group_of(f["name"])
		if not groups.has(g):
			groups.append(g)
	var names := PackedStringArray()
	for g in groups:
		for f in fields:
			if group_of(f["name"]) == g:
				names.append(f["name"])
	return names


## 기본으로 보여 줄 필드: 고도, 없으면 첫 필드.
func default_field() -> String:
	if has_field(ELEVATION_FIELD):
		return ELEVATION_FIELD
	var names := ordered_field_names()
	return names[0] if names.size() > 0 else ""


func label_of(name: String) -> String:
	return str(field(name).get("label", name))


func group_of(name: String) -> String:
	return str(field(name).get("group", "기타"))


## globe.json 에 적힌 단위 (로그 필드는 저장한 값의 단위, 예: "log10 m³/s").
func unit_of(name: String) -> String:
	return str(field(name).get("unit", ""))


## 보여 줄 때 쓰는 단위. 로그 필드는 10^값 의 단위 (예: "m³/s").
func display_unit_of(name: String) -> String:
	var unit := unit_of(name)
	if is_log(name):
		unit = unit.trim_prefix("log10").strip_edges()
		if unit.begins_with("(") and unit.ends_with(")"):
			unit = unit.substr(1, unit.length() - 2)
	return unit


func is_categorical(name: String) -> bool:
	return str(field(name).get("kind", "continuous")) == "categorical"


## 저장한 값이 log10 인 필드인지.
func is_log(name: String) -> bool:
	return bool(field(name).get("log", false))


## 연속 필드의 색 막대 범위 [min, max] (globe.json, 색 지도의 양 끝). 없으면 색 지도 범위.
## 이 범위 밖의 값은 끝 색으로 칠합니다.
func value_range(name: String) -> Vector2:
	var f := field(name)
	if f.has("min") and f.has("max") and f["min"] != null and f["max"] != null:
		return Vector2(float(f["min"]), float(f["max"]))
	var cm := _colormap(name)
	var values: PackedFloat64Array = cm["values"]
	if values.is_empty():
		return Vector2.ZERO
	return Vector2(values[0], values[values.size() - 1])


## 실제 자료의 범위 [data_min, data_max] (globe.json). 없으면 NAN.
func data_range(name: String) -> Vector2:
	var f := field(name)
	var lo: Variant = f.get("data_min")
	var hi: Variant = f.get("data_max")
	return Vector2(NAN if lo == null else float(lo), NAN if hi == null else float(hi))


## 값이 없는 칸의 글. 필드에 nan_label 이 있으면 덧붙입니다 (예: '값 없음 · 바다').
func no_data_text(name: String) -> String:
	var why: Variant = field(name).get("nan_label")
	if why == null or str(why).is_empty() or str(why) == NO_DATA_TEXT:
		return NO_DATA_TEXT
	return NO_DATA_TEXT + " · " + str(why)


## 범주가 덮은 표면 넓이의 몫 (0~1, globe.json 의 area_fraction). 없으면 NAN.
func area_fraction(name: String, id: int) -> float:
	for cat in categories(name):
		if cat is Dictionary and int(cat.get("id", -1)) == id:
			var frac: Variant = cat.get("area_fraction")
			return NAN if frac == null else float(frac)
	return NAN


## 범주 넓이 몫의 잣대를 설명하는 글 (globe.json 의 area_fraction_note, 없으면 빈 글).
func area_fraction_note(name: String) -> String:
	var note: Variant = field(name).get("area_fraction_note")
	return "" if note == null else str(note)


## 범주 필드의 범주 [{"id", "label", "rgb"}] (globe.json 순서).
func categories(name: String) -> Array:
	return field(name).get("categories", [])


## 범주가 '값 없음'(categories 의 no_data: true, 예: 바다의 지질)인지.
func is_no_data_category(name: String, id: int) -> bool:
	var cats: Dictionary = _category_table(name)
	return cats.has(id) and bool(cats[id]["no_data"])


## 연속 필드의 색이 끊기는 값 (globe.json 의 color_break, 없으면 NAN). 이 값의 양쪽 칸은 섞지 않습니다.
func color_break(name: String) -> float:
	var v: Variant = field(name).get("color_break")
	return float(v) if (v is float or v is int) else NAN


## 범주 필드의 드문 칸을 화면에 점으로도 찍는지 (globe.json 의 point_markers).
func has_point_markers(name: String) -> bool:
	return is_categorical(name) and field(name).get("point_markers") == true


## 점으로 찍을 칸 번호 (f·N² + r·N + c): 범주 0 도 '값 없음' 범주도 아닌 칸. 처음 부를 때 셉니다.
func marker_cells(name: String) -> PackedInt32Array:
	if _marker_cells.has(name):
		return _marker_cells[name]
	var out := PackedInt32Array()
	var arr: Variant = _values.get(name)
	if has_point_markers(name) and arr is PackedByteArray:
		var bytes: PackedByteArray = arr
		for cat in categories(name):
			if not (cat is Dictionary) or not cat.has("id"):
				continue
			var id := int(cat["id"])
			if id <= 0 or id > 255 or is_no_data_category(name, id):
				continue
			# find 는 엔진 안에서 훑으므로, 드문 칸만 GDScript 로 셉니다.
			var i := bytes.find(id)
			while i != -1:
				out.append(i)
				i = bytes.find(id, i + 1)
		out.sort()
	_marker_cells[name] = out
	return out


## 텍스처 알파(텍셀의 종류) 하나: 값 없음 TEXEL_NO_DATA, color_break 아래 TEXEL_BELOW_BREAK,
## 그 밖 TEXEL_VALUE. 범주 필드는 늘 TEXEL_VALUE 입니다.
func texel_kind(name: String, value: float) -> int:
	if is_categorical(name):
		return TEXEL_VALUE
	if is_nan(value):
		return TEXEL_NO_DATA
	var brk := color_break(name)
	if not is_nan(brk) and value < brk:
		return TEXEL_BELOW_BREAK
	return TEXEL_VALUE


func category_label(name: String, id: int) -> String:
	var cats: Dictionary = _category_table(name)
	if cats.has(id):
		return cats[id]["label"]
	return "번호 %d" % id


func category_color(name: String, id: int) -> Color:
	var cats: Dictionary = _category_table(name)
	if cats.has(id):
		return cats[id]["color"]
	return no_data_color


## 필드의 저장 값 배열 (PackedFloat32Array 또는 PackedByteArray, 6·N² 개).
func values(name: String) -> Variant:
	return _values.get(name)


func cell_index(face: int, row: int, col: int) -> int:
	return (face * face_res + row) * face_res + col


## 칸 하나의 저장 값. 값이 없으면 NAN. 범주 필드는 번호를 float 로 돌려줍니다.
func raw_value(name: String, face: int, row: int, col: int) -> float:
	var arr: Variant = _values.get(name)
	if arr == null:
		return NAN
	return float(arr[cell_index(face, row, col)])


## 방향이 가리키는 칸의 저장 값 (NAN = 값 없음).
func value_at(name: String, dir: Vector3) -> float:
	var cell := direction_to_cell(dir)
	return raw_value(name, cell["face"], cell["row"], cell["col"])


## 값 한 개를 단위와 함께 글로 (범주 필드는 범주 이름, NaN 은 '값 없음').
func format_value(name: String, value: float) -> String:
	if is_nan(value):
		return no_data_text(name)
	if is_categorical(name):
		return category_label(name, int(value))
	var unit := display_unit_of(name)
	var text := format_number(pow(10.0, value) if is_log(name) else value)
	return text if unit.is_empty() else text + " " + unit


# ---------------------------------------------------------------- 겹쳐 보기

func has_overlay(name: String) -> bool:
	return _overlay_values.has(name)


func overlay(name: String) -> Dictionary:
	for o in overlays:
		if o["name"] == name:
			return o
	return {}


## 방향이 가리키는 칸의 겹쳐 보기 값 (강은 유량 등급 1~4, 호수는 1, 없으면 0).
func overlay_value_at(name: String, dir: Vector3) -> int:
	if not _overlay_values.has(name):
		return 0
	var cell := direction_to_cell(dir)
	var arr: PackedByteArray = _overlay_values[name]
	return arr[cell_index(cell["face"], cell["row"], cell["col"])]


## 방향이 가리키는 칸에 겹쳐 보기(강, 호수)가 있는지.
func overlay_at(name: String, dir: Vector3) -> bool:
	return overlay_value_at(name, dir) != 0


## 겹쳐 보기 값의 이름 (overlays[].categories, 없으면 '있음'/'없음').
func overlay_value_label(name: String, value: int) -> String:
	for cat in overlay(name).get("categories", []):
		if cat is Dictionary and int(cat.get("id", -1)) == value:
			return str(cat.get("label", value))
	return "있음" if value != 0 else "없음"


## 겹쳐 보기 값의 색 (overlays[].categories 의 rgb, 없으면 overlays[].rgb, 그것도 없으면 fallback).
func overlay_value_color(name: String, value: int, fallback: Color) -> Color:
	var o := overlay(name)
	for cat in o.get("categories", []):
		if cat is Dictionary and int(cat.get("id", -1)) == value and cat.get("rgb") is Array:
			var rgb: Array = cat["rgb"]
			return Color8(int(rgb[0]), int(rgb[1]), int(rgb[2]))
	if o.get("rgb") is Array:
		var c: Array = o["rgb"]
		return Color8(int(c[0]), int(c[1]), int(c[2]))
	return fallback


# ---------------------------------------------------------------- 격자

## 방향 → 칸 {"face", "row", "col", "a", "b"}. 면은 n 과의 내적이 가장 큰 면입니다.
func direction_to_cell(dir: Vector3) -> Dictionary:
	var d := dir.normalized()
	var face := 0
	var best := -INF
	for f in FACE_COUNT:
		var k := d.dot(face_n[f])
		if k > best:
			best = k
			face = f
	var a := atan(d.dot(face_u[face]) / best) / QUARTER_PI
	var b := atan(d.dot(face_v[face]) / best) / QUARTER_PI
	var col := clampi(floori((a + 1.0) * 0.5 * face_res), 0, face_res - 1)
	var row := clampi(floori((b + 1.0) * 0.5 * face_res), 0, face_res - 1)
	return {"face": face, "row": row, "col": col, "a": a, "b": b}


## 칸 중심의 단위 방향.
func cell_dir(face: int, row: int, col: int) -> Vector3:
	var a := -1.0 + 2.0 * (float(col) + 0.5) / float(face_res)
	var b := -1.0 + 2.0 * (float(row) + 0.5) / float(face_res)
	return face_point(face, a, b)


## 모서리의 단위 방향.
func corner_dir(face: int, row: int, col: int) -> Vector3:
	var a := -1.0 + 2.0 * float(col) / float(face_res)
	var b := -1.0 + 2.0 * float(row) / float(face_res)
	return face_point(face, a, b)


## 면 좌표 (a, b) → 단위 방향 (등각 사상).
func face_point(face: int, a: float, b: float) -> Vector3:
	return (face_n[face] + tan_quarter(a) * face_u[face] + tan_quarter(b) * face_v[face]) \
			.normalized()


## 모서리 고도를 쌍선형으로 보간한 고도 (m). 지구본 메시의 높이와 거의 같습니다.
func corner_elevation_at(dir: Vector3) -> float:
	if corner_elevation_m.is_empty():
		return 0.0
	var cell := direction_to_cell(dir)
	var side := face_res + 1
	var x := clampf((float(cell["a"]) + 1.0) * 0.5 * face_res, 0.0, float(face_res))
	var y := clampf((float(cell["b"]) + 1.0) * 0.5 * face_res, 0.0, float(face_res))
	var c0 := mini(int(x), face_res - 1)
	var r0 := mini(int(y), face_res - 1)
	var tx := x - float(c0)
	var ty := y - float(r0)
	var i := int(cell["face"]) * side * side + r0 * side + c0
	var top := lerpf(corner_elevation_m[i], corner_elevation_m[i + 1], tx)
	var bottom := lerpf(corner_elevation_m[i + side], corner_elevation_m[i + side + 1], tx)
	return lerpf(top, bottom, ty)


## 칸 모서리 넷 가운데 가장 높은 고도 (m). 칸 안 어디서든 메시가 이보다 높지 않습니다.
func cell_top_elevation(face: int, row: int, col: int) -> float:
	if corner_elevation_m.is_empty():
		return sea_level_m
	var side := face_res + 1
	var i := face * side * side + row * side + col
	return maxf(maxf(corner_elevation_m[i], corner_elevation_m[i + 1]),
			maxf(corner_elevation_m[i + side], corner_elevation_m[i + side + 1]))


## 히어로 유역의 단위 방향 (없으면 Vector3.ZERO).
func hero_direction() -> Vector3:
	if hero.is_empty() or not hero.has("unit"):
		return Vector3.ZERO
	var u: Array = hero["unit"]
	return Vector3(float(u[0]), float(u[1]), float(u[2])).normalized()


# ---------------------------------------------------------------- 텍스처

## 값 하나의 색 (범주 필드는 범주 색, NaN 은 회색).
func color_of(name: String, value: float) -> Color:
	if is_nan(value):
		return no_data_color
	if is_categorical(name):
		return category_color(name, int(value))
	var cm := _colormap(name)
	var values: PackedFloat64Array = cm["values"]
	if values.is_empty():
		return no_data_color
	return _interpolate(values, cm["colors"], _start_of(cm, value), value)


## 필드의 면별 RGBA8 텍스처 6장 (N × N, 픽셀 (x, y) = 칸 (col, row)). 처음 부를 때 만듭니다.
## RGB 는 범례 색(sRGB), A 는 텍셀의 종류(texel_kind)입니다.
func field_textures(name: String) -> Array[ImageTexture]:
	if _textures.has(name):
		return _textures[name]
	var out: Array[ImageTexture] = []
	if not _values.has(name):
		return out
	var nn := face_res * face_res
	for f in FACE_COUNT:
		var bytes: PackedByteArray
		if is_categorical(name):
			bytes = _categorical_bytes(name, f * nn, nn)
		else:
			bytes = _continuous_bytes(name, f * nn, nn)
		var image := Image.create_from_data(face_res, face_res, false, Image.FORMAT_RGBA8, bytes)
		out.append(ImageTexture.create_from_image(image))
	_textures[name] = out
	return out


## 면별 모서리 고도 텍스처 6장 (FORMAT_RF, (N+1) × (N+1), 단위 m).
func corner_textures() -> Array[ImageTexture]:
	if not _corner_textures.is_empty() or corner_elevation_m.is_empty():
		return _corner_textures
	var side := face_res + 1
	var count := side * side
	for f in FACE_COUNT:
		var bytes := corner_elevation_m.slice(f * count, (f + 1) * count).to_byte_array()
		var image := Image.create_from_data(side, side, false, Image.FORMAT_RF, bytes)
		_corner_textures.append(ImageTexture.create_from_image(image))
	return _corner_textures


## 면별 겹쳐 보기 텍스처 6장 (RGBA8, N × N). R = 강 값(유량 등급 0~4), G = 호수 값(0, 1)을
## 바이트 그대로 넣습니다. 셰이더가 등급별 색과 '몇 등급부터 그릴지'를 정합니다.
func overlay_textures() -> Array[ImageTexture]:
	if not _overlay_textures.is_empty():
		return _overlay_textures
	var nn := face_res * face_res
	var rivers: PackedByteArray = _overlay_values.get("rivers", PackedByteArray())
	var lakes: PackedByteArray = _overlay_values.get("lakes", PackedByteArray())
	for f in FACE_COUNT:
		var bytes := PackedByteArray()
		bytes.resize(nn * 4)
		var base := f * nn
		for i in nn:
			var k := i * 4
			bytes[k] = 0 if rivers.is_empty() else rivers[base + i]
			bytes[k + 1] = 0 if lakes.is_empty() else lakes[base + i]
			bytes[k + 3] = 255
		var image := Image.create_from_data(face_res, face_res, false, Image.FORMAT_RGBA8, bytes)
		_overlay_textures.append(ImageTexture.create_from_image(image))
	return _overlay_textures


## 연속 필드의 범례 막대 그림 (width × 1, 왼쪽 = min, 오른쪽 = max).
func legend_image(name: String, width: int = 256) -> Image:
	var image := Image.create_empty(width, 1, false, Image.FORMAT_RGBA8)
	var r := value_range(name)
	for x in width:
		var t := (float(x) + 0.5) / float(width)
		image.set_pixel(x, 0, color_of(name, lerpf(r.x, r.y, t)))
	return image


# ---------------------------------------------------------------- 읽기

func _read(dir: String) -> void:
	folder = dir
	var json_path := dir.path_join(MANIFEST)
	if not FileAccess.file_exists(json_path):
		error_message = "지구본 설명 파일이 없습니다: %s" % json_path
		missing = true
		return
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(json_path))
	if not (parsed is Dictionary):
		error_message = "지구본 설명 파일을 JSON 으로 읽지 못했습니다: %s" % json_path
		return
	meta = parsed
	for key in ["format", "format_version", "face_res", "faces", "fields", "corners"]:
		if not meta.has(key):
			error_message = "%s 에 '%s' 가 없습니다" % [json_path, key]
			return
	if meta["format"] != FORMAT:
		error_message = "지구본 형식이 아닙니다: format = %s (기대 %s)" % [meta["format"], FORMAT]
		return
	if int(meta["format_version"]) > FORMAT_VERSION:
		error_message = "지구본 형식 버전 %s 을 읽을 수 없습니다 (읽을 수 있는 버전 %d 이하)" % [
			meta["format_version"], FORMAT_VERSION]
		return
	face_res = int(meta["face_res"])
	if face_res < 2:
		error_message = "face_res = %d 가 너무 작습니다" % face_res
		return
	radius_m = float(meta.get("radius_m", radius_m))
	var sea: Variant = meta.get("sea_level_m", 0.0)
	sea_level_m = 0.0 if sea == null else float(sea)
	var nan_rgb: Variant = meta.get("nan_rgb")
	if nan_rgb is Array and nan_rgb.size() >= 3:
		no_data_color = Color8(int(nan_rgb[0]), int(nan_rgb[1]), int(nan_rgb[2]))
	if not _read_faces(meta["faces"]):
		return
	if not _read_corners(meta["corners"]):
		return
	for f in meta["fields"]:
		if not (f is Dictionary) or not _read_field(f):
			if error_message.is_empty():
				error_message = "fields 의 항목이 사전이 아닙니다"
			return
	if fields.is_empty():
		error_message = "%s 에 필드가 하나도 없습니다" % json_path
		return
	for o in meta.get("overlays", []):
		if not (o is Dictionary) or not _read_overlay(o):
			if error_message.is_empty():
				error_message = "overlays 의 항목이 사전이 아닙니다"
			return
	var h: Variant = meta.get("hero")
	if h is Dictionary and h.has("unit"):
		hero = h


func _read_faces(faces: Variant) -> bool:
	if not (faces is Array) or faces.size() != FACE_COUNT:
		error_message = "faces 는 면 6개의 배열이어야 합니다"
		return false
	face_n.resize(FACE_COUNT)
	face_u.resize(FACE_COUNT)
	face_v.resize(FACE_COUNT)
	var seen := {}
	for i in FACE_COUNT:
		var entry: Variant = faces[i]
		if not (entry is Dictionary):
			error_message = "faces[%d] 가 사전이 아닙니다" % i
			return false
		var index := int(entry.get("index", i))
		if index < 0 or index >= FACE_COUNT or seen.has(index):
			error_message = "faces 의 index %d 가 잘못됐습니다" % index
			return false
		seen[index] = true
		for key in ["n", "u", "v"]:
			var vec: Variant = entry.get(key)
			if not (vec is Array) or vec.size() != 3:
				error_message = "faces[%d].%s 가 길이 3 인 배열이 아닙니다" % [i, key]
				return false
		face_n[index] = _vec3(entry["n"])
		face_u[index] = _vec3(entry["u"])
		face_v[index] = _vec3(entry["v"])
	return true


func _read_corners(info: Variant) -> bool:
	if not (info is Dictionary) or not info.has("file"):
		error_message = "corners 에 file 이 없습니다"
		return false
	var side := face_res + 1
	if not _check_shape(info, [FACE_COUNT, side, side], "corners"):
		return false
	var bytes := _read_bin(str(info["file"]), str(info.get("dtype", "float32")),
			FACE_COUNT * side * side)
	if bytes.is_empty():
		return false
	corner_elevation_m = bytes.to_float32_array()
	corner_min_m = INF
	corner_max_m = -INF
	for i in corner_elevation_m.size():
		var z := corner_elevation_m[i]
		if is_nan(z) or is_inf(z):
			corner_elevation_m[i] = sea_level_m
			continue
		corner_min_m = minf(corner_min_m, z)
		corner_max_m = maxf(corner_max_m, z)
	if corner_min_m > corner_max_m:
		corner_min_m = sea_level_m
		corner_max_m = sea_level_m
	return true


func _read_field(f: Dictionary) -> bool:
	for key in ["name", "file", "dtype"]:
		if not f.has(key):
			error_message = "필드 항목에 '%s' 가 없습니다: %s" % [key, f.get("name", "?")]
			return false
	var name := str(f["name"])
	if _field_by_name.has(name):
		error_message = "필드 이름 '%s' 가 두 번 나옵니다" % name
		return false
	var kind := str(f.get("kind", "continuous"))
	if kind != "continuous" and kind != "categorical":
		error_message = "필드 '%s' 의 kind '%s' 를 모릅니다" % [name, kind]
		return false
	if kind == "categorical" and not (f.get("categories") is Array):
		error_message = "범주 필드 '%s' 에 categories 가 없습니다" % name
		return false
	if kind == "continuous" and not (f.get("colormap") is Array and f["colormap"].size() >= 1):
		error_message = "연속 필드 '%s' 에 colormap 이 없습니다" % name
		return false
	if not _check_shape(f, [FACE_COUNT, face_res, face_res], "필드 '%s'" % name):
		return false
	var count := FACE_COUNT * face_res * face_res
	var dtype := str(f["dtype"])
	var bytes := _read_bin(str(f["file"]), dtype, count)
	if bytes.is_empty():
		return false
	_values[name] = bytes.to_float32_array() if dtype == "float32" else bytes
	_field_by_name[name] = f
	fields.append(f)
	return true


func _read_overlay(o: Dictionary) -> bool:
	if not o.has("name") or not o.has("file"):
		error_message = "겹쳐 보기 항목에 name 이나 file 이 없습니다"
		return false
	var name := str(o["name"])
	if not _check_shape(o, [FACE_COUNT, face_res, face_res], "겹쳐 보기 '%s'" % name):
		return false
	var dtype := str(o.get("dtype", "uint8"))
	if dtype != "uint8":
		error_message = "겹쳐 보기 '%s' 의 dtype 은 uint8 이어야 합니다 (%s)" % [name, dtype]
		return false
	var bytes := _read_bin(str(o["file"]), dtype, FACE_COUNT * face_res * face_res)
	if bytes.is_empty():
		return false
	_overlay_values[name] = bytes
	overlays.append(o)
	return true


func _check_shape(info: Dictionary, expected: Array, what: String) -> bool:
	if not info.has("shape"):
		return true
	var shape: Array = info["shape"]
	var got := shape.map(func(x: Variant) -> int: return int(x))
	if got != expected:
		error_message = "%s 의 shape %s 가 %s 와 다릅니다" % [what, got, expected]
		return false
	return true


## .bin 을 읽어 크기를 확인합니다. 실패하면 빈 배열과 error_message.
func _read_bin(file: String, dtype: String, count: int) -> PackedByteArray:
	var item_size := 0
	match dtype:
		"float32":
			item_size = 4
		"uint8":
			item_size = 1
		_:
			error_message = "%s 의 dtype '%s' 를 읽을 수 없습니다 (float32, uint8 만)" % [file, dtype]
			return PackedByteArray()
	var path := folder.path_join(file)
	if not FileAccess.file_exists(path):
		error_message = "지구본 파일이 없습니다: %s" % path
		return PackedByteArray()
	var bytes := FileAccess.get_file_as_bytes(path)
	if bytes.size() != count * item_size:
		error_message = "%s 크기 %d 바이트가 기대한 %d 바이트(%d 개 × %d)와 다릅니다" % [
			path, bytes.size(), count * item_size, count, item_size]
		return PackedByteArray()
	return bytes


# ---------------------------------------------------------------- 색 계산

func _colormap(name: String) -> Dictionary:
	if _colormaps.has(name):
		return _colormaps[name]
	var values := PackedFloat64Array()
	var colors := PackedColorArray()
	for knot in field(name).get("colormap", []):
		if knot is Array and knot.size() == 2 and knot[1] is Array and knot[1].size() >= 3:
			values.append(float(knot[0]))
			var rgb: Array = knot[1]
			colors.append(Color8(int(rgb[0]), int(rgb[1]), int(rgb[2])))
	var cm := {"values": values, "colors": colors, "starts": PackedInt32Array(),
			"lo": 0.0, "inv_width": 0.0}
	var k := values.size()
	if k >= 2 and values[k - 1] > values[0]:
		# 칸 b 가 시작하는 값 이하인 마지막 매듭 번호 (k - 2 를 넘지 않음)
		var starts := PackedInt32Array()
		starts.resize(_LUT_BINS)
		var width := (values[k - 1] - values[0]) / float(_LUT_BINS)
		var i := 0
		for b in _LUT_BINS:
			var at := values[0] + float(b) * width
			while i + 1 <= k - 2 and values[i + 1] <= at:
				i += 1
			starts[b] = i
		cm["starts"] = starts
		cm["lo"] = values[0]
		cm["inv_width"] = 1.0 / width
	_colormaps[name] = cm
	return cm


func _start_of(cm: Dictionary, v: float) -> int:
	var starts: PackedInt32Array = cm["starts"]
	if starts.is_empty():
		return 0
	return starts[clampi(int((v - float(cm["lo"])) * float(cm["inv_width"])), 0, _LUT_BINS - 1)]


## 매듭 [values, colors] 사이를 선형 보간합니다. start 는 values[start] <= v 인 매듭 번호입니다.
## 약속(engine/GLOBE.md)은 값이 엄격히 늘어나는 것이고, 해수면의 끊김은 -0.5 m 매듭(얕은 바다)과
## 0 m 매듭(낮은 땅) 사이 0.5 m 와 color_break 로 나타냅니다. 같은 값의 매듭이 둘 와도 멈추지 않고
## 그 값부터 뒤 매듭의 색을 씁니다.
static func _interpolate(values: PackedFloat64Array, colors: PackedColorArray, start: int,
		v: float) -> Color:
	var k := values.size()
	if k == 1 or v < values[0]:
		return colors[0]
	if v >= values[k - 1]:
		return colors[k - 1]
	var i := start
	while i + 1 < k and values[i + 1] <= v:
		i += 1
	var span := values[i + 1] - values[i]
	var t := (v - values[i]) / span if span > 0.0 else 1.0
	return colors[i].lerp(colors[i + 1], t)


func _continuous_bytes(name: String, base: int, count: int) -> PackedByteArray:
	var arr: Variant = _values[name]
	var cm := _colormap(name)
	var values: PackedFloat64Array = cm["values"]
	var colors: PackedColorArray = cm["colors"]
	var starts: PackedInt32Array = cm["starts"]
	var lo: float = cm["lo"]
	var inv_width: float = cm["inv_width"]
	var brk := color_break(name)
	var has_break := not is_nan(brk)
	var bytes := PackedByteArray()
	bytes.resize(count * 4)
	for i in count:
		var v := float(arr[base + i])
		var c := no_data_color
		var kind := TEXEL_NO_DATA
		if not is_nan(v) and not values.is_empty():
			var start := 0
			if not starts.is_empty():
				start = starts[clampi(int((v - lo) * inv_width), 0, _LUT_BINS - 1)]
			c = _interpolate(values, colors, start, v)
			kind = TEXEL_BELOW_BREAK if has_break and v < brk else TEXEL_VALUE
		var k := i * 4
		bytes[k] = c.r8
		bytes[k + 1] = c.g8
		bytes[k + 2] = c.b8
		bytes[k + 3] = kind
	return bytes


func _categorical_bytes(name: String, base: int, count: int) -> PackedByteArray:
	var arr: Variant = _values[name]
	# 번호 0~255 → RGBA. 표에 없는 번호는 회색.
	var lut := PackedByteArray()
	lut.resize(256 * 4)
	for id in 256:
		var c := category_color(name, id)
		lut[id * 4] = c.r8
		lut[id * 4 + 1] = c.g8
		lut[id * 4 + 2] = c.b8
		lut[id * 4 + 3] = 255
	var grey := no_data_color
	var bytes := PackedByteArray()
	bytes.resize(count * 4)
	for i in count:
		var v := float(arr[base + i])
		var k := i * 4
		if is_nan(v) or v < 0.0 or v > 255.0:
			bytes[k] = grey.r8
			bytes[k + 1] = grey.g8
			bytes[k + 2] = grey.b8
			bytes[k + 3] = 255
			continue
		var j := int(v) * 4
		bytes[k] = lut[j]
		bytes[k + 1] = lut[j + 1]
		bytes[k + 2] = lut[j + 2]
		bytes[k + 3] = 255
	return bytes


func _category_table(name: String) -> Dictionary:
	if _categories.has(name):
		return _categories[name]
	var table := {}
	for cat in categories(name):
		if not (cat is Dictionary) or not cat.has("id"):
			continue
		# rgb 가 없거나 null 이거나 짧으면 '값 없음' 색으로 칠합니다.
		var rgb: Variant = cat.get("rgb")
		var color := no_data_color
		if rgb is Array and rgb.size() >= 3:
			color = Color8(int(rgb[0]), int(rgb[1]), int(rgb[2]))
		table[int(cat["id"])] = {
			"label": str(cat.get("label", "번호 %d" % int(cat["id"]))),
			"color": color,
			"no_data": cat.get("no_data") == true,
		}
	_categories[name] = table
	return table


static func tan_quarter(a: float) -> float:
	# 면 경계(a = ±1)는 정확히 ±1 로 둡니다. 이웃 면과 꼭짓점이 비트 단위로 같아야 틈이 없습니다.
	if a >= 1.0:
		return 1.0
	if a <= -1.0:
		return -1.0
	return tan(a * QUARTER_PI)


## + 0.0 은 -0.0 을 0.0 으로 바꿉니다 (면 경계 꼭짓점이 이웃 면과 비트 단위로 같도록).
static func _vec3(a: Array) -> Vector3:
	return Vector3(float(a[0]) + 0.0, float(a[1]) + 0.0, float(a[2]) + 0.0)


static func _group_thousands(n: int) -> String:
	var digits := str(absi(n))
	var out := ""
	while digits.length() > 3:
		out = "," + digits.substr(digits.length() - 3) + out
		digits = digits.substr(0, digits.length() - 3)
	return ("-" if n < 0 else "") + digits + out


static func _scientific(v: float) -> String:
	var exponent := floori(log(absf(v)) / log(10.0))
	var mantissa := v / pow(10.0, exponent)
	if absf(mantissa) >= 9.995:
		mantissa /= 10.0
		exponent += 1
	return "%.2f×10%s" % [mantissa, _superscript(exponent)]


static func _superscript(n: int) -> String:
	const DIGITS := ["⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹"]
	var out := "⁻" if n < 0 else ""
	for ch in str(absi(n)):
		out += DIGITS[int(ch)]
	return out
