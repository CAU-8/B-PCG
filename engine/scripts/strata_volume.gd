class_name StrataVolume
extends RefCounted
## 재질 부피(strata.u8 + strata.json)와 그 윗면(strata_top)을 읽어 셰이더용 텍스처로 바꿉니다.
##
## strata.u8 은 uint8, 배치는 row_layer_col 입니다: 번호 = (행 · 층 수 + 층) · 열 수 + 열.[br]
## - 행이 늘면 +Z (북 → 남), 열이 늘면 +X, 층은 윗면에서 아래로 dy 씩 내려갑니다.[br]
## - 층 k 의 가운데 Y = (strata_top 값 + strata_top.origin.y) − (k + 0.5) · dy.[br]
## - 값: 0..11 암석(geology.rocks), 254 물, 255 공기(동굴).[br]
## 한 행(층 × 열)이 바이트로 이어져 있어 3D 텍스처의 한 장(가로 = 열, 세로 = 층)이 됩니다.

const FORMAT := "uint8"
const LAYOUT := "row_layer_col"
const WATER_ID := 254
const AIR_ID := 255
## 3D 텍스처 한 변의 상한 (Metal·Vulkan 이 보장하는 값).
const MAX_TEXTURE_3D_SIZE := 2048
## 재질 번호 → 한국어 이름.
const KOREAN_NAMES := {
	"alluvium": "충적층", "sandstone": "사암", "shale": "셰일", "limestone": "석회암",
	"granite": "화강암", "volcanic": "화산암", "slate": "점판암", "schist": "편암",
	"gneiss": "편마암", "marble": "대리암", "quartzite": "규암", "soil": "흙",
	"water": "물", "air": "공기(동굴)",
}

## 행 수 (Z), 층 수 (아래로), 열 수 (X).
var rows: int = 0
var layers: int = 0
var cols: int = 0
## (dx, dy, dz) m.
var spacing := Vector3.ZERO
## 0번 열의 X 와 0번 행의 Z (m).
var origin_xz := Vector2.ZERO
## 재질 번호 (바이트 그대로).
var data := PackedByteArray()
## 윗면 높이맵 (strata_top).
var top: HeightmapLoader
## 재질 번호 → 영어 이름, 색.
var names := {}
var colors := {}
## 셰이더용 텍스처.
var texture: ImageTexture3D
var top_texture: ImageTexture
var palette: ImageTexture
var error_message: String = ""


## dir 아래 strata.json, strata.u8, strata_top.* 을 읽습니다.
static func load_dir(dir: String) -> StrataVolume:
	var volume := StrataVolume.new()
	volume._read(dir)
	return volume


func is_valid() -> bool:
	return error_message.is_empty()


## 구운 깊이 (m).
func depth_m() -> float:
	return float(layers) * spacing.y


## 셰이더 재질에 strata.gdshaderinc 의 uniform 을 채웁니다.
func apply_to(material: ShaderMaterial) -> void:
	material.set_shader_parameter("has_strata", true)
	material.set_shader_parameter("strata", texture)
	material.set_shader_parameter("strata_top", top_texture)
	material.set_shader_parameter("palette", palette)
	material.set_shader_parameter(
			"strata_origin", Vector3(origin_xz.x, top.origin.y, origin_xz.y))
	material.set_shader_parameter("strata_spacing", spacing)
	material.set_shader_parameter("strata_size", Vector3(cols, layers, rows))


## 전역 위치의 재질 번호. 부피 밖(옆·아래)이면 -1, 윗면 위면 AIR_ID.
## 셰이더와 달리 가장 가까운 기둥의 윗면을 써서 굽기 값과 정확히 같습니다.
func id_at(p: Vector3) -> int:
	if not is_valid():
		return -1
	var col := roundi((p.x - origin_xz.x) / spacing.x)
	var row := roundi((p.z - origin_xz.y) / spacing.z)
	if col < 0 or row < 0 or col >= cols or row >= rows:
		return -1
	var top_y := top.heights[row * cols + col] + top.origin.y
	var layer := floori((top_y - p.y) / spacing.y)
	if layer < 0:
		return AIR_ID
	if layer >= layers:
		return -1
	return data[(row * layers + layer) * cols + col]


## 재질 번호의 한국어 이름 (모르면 "번호 N").
func name_of(id: int) -> String:
	var english: String = names.get(id, "")
	return KOREAN_NAMES.get(english, "번호 %d" % id)


func _read(dir: String) -> void:
	var json_path := dir.path_join("strata.json")
	var raw_path := dir.path_join("strata.u8")
	if not FileAccess.file_exists(json_path) or not FileAccess.file_exists(raw_path):
		error_message = "재질 부피 파일이 없습니다: %s" % raw_path
		return
	var meta: Variant = JSON.parse_string(FileAccess.get_file_as_string(json_path))
	if not (meta is Dictionary):
		error_message = "%s 를 JSON 으로 읽지 못했습니다" % json_path
		return
	if meta.get("format") != FORMAT or meta.get("layout") != LAYOUT:
		error_message = "지원하지 않는 재질 부피 형식입니다: %s, %s" % [
			meta.get("format"), meta.get("layout")]
		return
	var shape: Array = meta["shape"]
	rows = int(shape[0])
	layers = int(shape[1])
	cols = int(shape[2])
	var sp: Dictionary = meta["spacing_m"]
	spacing = Vector3(float(sp["x"]), float(sp["y"]), float(sp["z"]))
	var o: Array = meta["origin_xz"]
	origin_xz = Vector2(float(o[0]), float(o[1]))
	if maxi(rows, maxi(layers, cols)) > MAX_TEXTURE_3D_SIZE:
		error_message = "재질 부피 %d × %d × %d 가 3D 텍스처 한도 %d 를 넘습니다" % [
			cols, layers, rows, MAX_TEXTURE_3D_SIZE]
		return

	data = FileAccess.get_file_as_bytes(raw_path)
	if data.size() != rows * layers * cols:
		error_message = "%s 크기 %d 가 %d × %d × %d 와 다릅니다" % [
			raw_path, data.size(), rows, layers, cols]
		return
	top = HeightmapLoader.load_stem(dir.path_join(str(meta.get("top_stem", "strata_top"))))
	if not top.is_valid():
		error_message = "재질 부피 윗면을 읽지 못했습니다: " + top.error_message
		return
	if top.width != cols or top.height != rows:
		error_message = "윗면 %d × %d 가 재질 부피 열 × 행 %d × %d 와 다릅니다" % [
			top.width, top.height, cols, rows]
		return

	for entry in meta.get("legend", []):
		var id := int(entry["id"])
		names[id] = str(entry["name"])
		var rgb: Array = entry["rgb"]
		colors[id] = Color8(int(rgb[0]), int(rgb[1]), int(rgb[2]))
	_build_textures()


func _build_textures() -> void:
	var slice_bytes := layers * cols
	var images: Array[Image] = []
	images.resize(rows)
	for row in rows:
		images[row] = Image.create_from_data(
				cols, layers, false, Image.FORMAT_R8,
				data.slice(row * slice_bytes, (row + 1) * slice_bytes))
	texture = ImageTexture3D.new()
	var err := texture.create(Image.FORMAT_R8, cols, layers, rows, false, images)
	if err != OK:
		error_message = "3D 텍스처를 만들지 못했습니다 (오류 %d)" % err
		return
	top_texture = ImageTexture.create_from_image(Image.create_from_data(
			top.width, top.height, false, Image.FORMAT_RF, top.heights.to_byte_array()))
	var pal := Image.create(256, 1, false, Image.FORMAT_RGBA8)
	for id in colors:
		pal.set_pixel(int(id), 0, colors[id])
	palette = ImageTexture.create_from_image(pal)
