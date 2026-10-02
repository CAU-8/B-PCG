extends SceneTree
## 화면 캡처. 시작 장면을 여러 시점·레이어 조합으로 찍어 PNG 로 남깁니다 (보고서, 리뷰용).
##
## 창이 떠야 그려지므로 --headless 를 쓰지 않습니다. 편집기 설정을 덮어쓰지 않게 HOME 을
## 임시 폴더로 바꿔 돌립니다 (README '연기 검사' 참고).[br]
##   HOME=$(mktemp -d) godot --path engine --resolution 1600x900 \\[br]
##     --script res://tests/screenshots.gd -- --shots-dir=/절대/경로[br]
## 구운 묶음이 있어야 합니다 (기본 res://baked, 또는 --baked-dir=<폴더>).
## 끝나면 'BPCG_SHOTS_OK <장 수>' 를 찍고 끝납니다.

const MAIN_SCENE := "res://scenes/main.tscn"
const GLOBE_SCENE := "res://scenes/globe.tscn"
const ARG_PREFIX := "--shots-dir="
## 한 장을 찍기 전에 기다리는 프레임 수 (셰이더 컴파일, 그림자, 안개가 자리 잡도록).
const SETTLE_FRAMES := 12

var _dir := ""
var _count := 0


func _initialize() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(ARG_PREFIX):
			_dir = arg.trim_prefix(ARG_PREFIX)
	if _dir.is_empty():
		print("BPCG_SHOTS_FAIL: --shots-dir=<폴더> 가 필요합니다")
		quit(1)
		return
	DirAccess.make_dir_recursive_absolute(_dir)
	_run()


func _run() -> void:
	await process_frame
	var main := (load(MAIN_SCENE) as PackedScene).instantiate()
	root.add_child(main)
	var baked := main.get_node("Baked") as BakedLayers
	var player := main.get_node("Player") as Player
	var terrain := main.get_node("Terrain") as HeightmapTerrain
	if not baked.loaded:
		print("BPCG_SHOTS_FAIL: 구운 묶음이 없습니다")
		quit(1)
		return
	var c := terrain.to_global(terrain.heightmap.center())
	var r := baked.corridor_rect

	# 1. 회랑 위 40 m, 북쪽을 봄 (시작 시점)
	await _shot("01_start", main)
	# 2. 높은 곳에서 회랑과 주변 25 m 지형
	player.look_from(Vector3(c.x + 1800.0, c.y + 1600.0, r.end.y + 1800.0), Vector3(c.x, c.y, c.z))
	await _shot("02_aerial", main)
	# 3. 같은 시점, 지질도 색
	main.cycle_color_mode()
	await _shot("03_aerial_geology", main)
	main.cycle_color_mode()
	# 4. 지층 단면: 지표 위 60 m 에서, 눈앞 50 m 에 동서로 선 면을 잘라 내려다봄
	var eye := Vector3(c.x, _ground(main, c) + 60.0, c.z + 50.0)
	player.look_from(eye, Vector3(c.x, _ground(main, c) - 80.0, c.z - 40.0))
	baked.set_section(true, Vector3(c.x, 0.0, c.z), Vector3.BACK)
	await _shot("04_section", main)
	# 5. 같은 단면을 동굴이 지나는 곳에서
	if baked.entrances_inside.size() > 0:
		var a := baked.entrances_inside[baked.entrances_inside.size() / 2]
		var g := _ground(main, a)
		player.look_from(Vector3(a.x + 20.0, g + 40.0, a.z + 70.0), Vector3(a.x, a.y - 10.0, a.z))
		baked.set_section(true, Vector3(a.x, 0.0, a.z + 2.0), Vector3.BACK)
		await _shot("04b_section_cave", main)
	baked.set_section(false)
	# 5. 동굴만 보기 (위에서 비스듬히)
	baked.solo("caves")
	player.look_from(Vector3(c.x + 300.0, c.y + 250.0, c.z + 300.0), Vector3(c.x, c.y - 50.0, c.z))
	await _shot("05_caves_solo", main)
	baked.show_all()
	# 6. 물과 지하수면만
	baked.solo("water")
	baked.set_layer_visible("water_table", true)
	player.look_from(Vector3(c.x + 500.0, c.y + 400.0, c.z + 600.0), Vector3(c.x, c.y, c.z))
	await _shot("06_water_and_table", main)
	baked.show_all()
	# 7. 첫 동굴 입구 앞
	if baked.entrances_inside.size() > 0:
		main.go_to_entrance(1)
		await _shot("07_entrance", main)
	# 8·9. 프랙탈 디테일 켬·끔 (같은 시점, 비탈 가까이)
	if baked.layer_ids().has("fractal_detail"):
		var p := Vector3(c.x + 150.0, 0.0, c.z - 400.0)
		var g := _ground(main, p)
		player.look_from(Vector3(p.x - 35.0, g + 18.0, p.z + 35.0), Vector3(p.x, g - 5.0, p.z))
		baked.set_layer_visible("fractal_detail", true)
		await _shot("08_detail_on", main)
		baked.set_layer_visible("fractal_detail", false)
		await _shot("09_detail_off", main)
		baked.set_layer_visible("fractal_detail", true)
	# 10~. 지구본 장면 (행성 전체): 고도, 판, 강수, 히어로로 날아가기
	await _globe_shots(main)
	print("BPCG_SHOTS_OK %d" % _count)
	quit(0)


func _ground(main: Node, p: Vector3) -> float:
	var g: float = main._ground_y(p)
	return g if is_finite(g) else p.y


func _shot(name: String, main: Node) -> void:
	for i in SETTLE_FRAMES:
		await process_frame
	main._update_status()
	await process_frame
	var image := root.get_viewport().get_texture().get_image()
	var path := _dir.path_join(name + ".png")
	image.save_png(path)
	_count += 1
	print("찍음: %s" % path)


func _globe_shots(main: Node) -> void:
	if not ResourceLoader.exists(GLOBE_SCENE):
		return
	main.queue_free()
	await process_frame
	var globe_main := (load(GLOBE_SCENE) as PackedScene).instantiate()
	root.add_child(globe_main)
	await process_frame
	if globe_main.globe.data == null or globe_main.globe.data.missing:
		print("지구본 자료가 없어 지구본 장면은 찍지 않습니다")
		return
	await _shot_plain("10_globe_elevation")
	for pair in [[2, "11_globe_plates"], [8, "12_globe_precip"]]:
		globe_main.select_field_index(pair[0])
		await _shot_plain(pair[1])
	globe_main.select_field_index(0)
	globe_main.camera.fly_to_hero()
	for i in 90:
		await process_frame
	await _shot_plain("13_globe_hero")


## 지구본 장면 캡처 (회랑 상태 글을 다시 쓰지 않음).
func _shot_plain(name: String) -> void:
	for i in SETTLE_FRAMES:
		await process_frame
	var image := root.get_viewport().get_texture().get_image()
	var path := _dir.path_join(name + ".png")
	image.save_png(path)
	_count += 1
	print("찍음: %s" % path)
