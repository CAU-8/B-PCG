extends Node3D
## 시작 장면. 지형이 다 만들어진 뒤 플레이어를 지형 가운데 지면 위에 세웁니다.
##
## 자식의 _ready() 가 부모보다 먼저 불리므로 여기서는 지형이 이미 준비돼 있습니다.

@onready var terrain: HeightmapTerrain = $Terrain
@onready var player: CharacterBody3D = $Player


func _ready() -> void:
	player.global_position = terrain.spawn_point()
