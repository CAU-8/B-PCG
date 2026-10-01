class_name HeightmapTerrain
extends Node3D
## 높이맵 한 장을 읽어 메시(MeshInstance3D)와 충돌체(StaticBody3D)를 자식으로 만듭니다.
##
## baked/ 는 Python 이 쓰는 폴더라 저장소에 없을 수 있습니다.
## heightmap_stem 이 없으면 fallback_stem (samples/ 의 작은 표본)을 대신 씁니다.

## 먼저 찾을 높이맵 (확장자 뺀 경로). Python 굽기 결과가 여기에 놓입니다.
@export var heightmap_stem: String = "res://baked/heightmap"
## heightmap_stem 이 없을 때 쓸 높이맵.
@export var fallback_stem: String = "res://samples/sample"
## 지형 메시에 씌울 재질. 비워 두면 기본 재질을 씁니다.
@export var material: Material

## 읽은 높이맵. 읽기에 실패하면 is_valid() 가 false 입니다.
var heightmap: HeightmapLoader
## 실제로 읽은 높이맵 경로.
var used_stem: String = ""


func _ready() -> void:
	rebuild()


## 높이맵을 다시 읽고 자식 노드를 새로 만듭니다. 성공하면 true.
func rebuild() -> bool:
	for child in get_children():
		remove_child(child)
		child.queue_free()

	var stem := heightmap_stem
	if not HeightmapLoader.exists(stem):
		print("지형: %s 가 없어 표본 %s 를 씁니다" % [stem, fallback_stem])
		stem = fallback_stem
	heightmap = HeightmapLoader.load_stem(stem)
	if not heightmap.is_valid():
		push_error("지형을 읽지 못했습니다: " + heightmap.error_message)
		return false
	used_stem = stem

	var mesh_instance := MeshInstance3D.new()
	mesh_instance.name = "Mesh"
	mesh_instance.mesh = heightmap.build_mesh()
	mesh_instance.position = heightmap.origin
	if material != null:
		mesh_instance.material_override = material
	add_child(mesh_instance)

	var body := StaticBody3D.new()
	body.name = "Body"
	var shape := CollisionShape3D.new()
	shape.name = "Shape"
	shape.shape = heightmap.build_shape()
	shape.transform = heightmap.shape_transform()
	body.add_child(shape)
	add_child(body)
	return true


## 이 노드 기준 국소 좌표 (x, z) 의 지면 높이 (m). 지형 밖이면 NAN.
func height_at(local_pos: Vector3) -> float:
	if heightmap == null:
		return NAN
	return heightmap.height_at(local_pos)


## 지형 가운데 지면 위 clearance_m 높이의 전역 좌표. 플레이어를 세울 때 씁니다.
func spawn_point(clearance_m: float = 2.0) -> Vector3:
	if heightmap == null or not heightmap.is_valid():
		return global_position + Vector3.UP * clearance_m
	return to_global(heightmap.center() + Vector3.UP * clearance_m)
