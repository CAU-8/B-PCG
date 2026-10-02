class_name GlobeHud
extends CanvasLayer
## 지구본 화면의 글과 패널.
##
## 왼쪽 위: 지금 상태와 마우스가 가리키는 자리의 위도·경도, 고도, 고른 레이어 값.
## 클릭하면 그 자리의 모든 레이어 값을 아래 상자에 고정해 보여 줍니다 (Esc 로 풀기).[br]
## 오른쪽 위: 레이어 패널. 지구본 설명, 무리(지형, 판·지각, 해양, 기후, 물, 지질, 동굴)별 레이어
## 버튼, 강·호수·경위선 켜기, 강 등급 문턱, 그늘·대기 빛(꾸밈) 켜기, 고도 과장, 히어로 유역으로
## 가기, 회랑으로 들어가기 (Tab 으로 숨김).[br]
## 오른쪽 아래: 범례. 레이어 이름과 단위, 색 막대(최소·가운데·최대) 또는 범주 색 목록(넓이 몫),
## 무엇을 보여 주는지와 색 읽는 법.[br]
## 왼쪽 아래: 조작 도움말 (H 로 숨김). 버튼은 키보드 초점을 받지 않아 Space 같은 키가 버튼을
## 누르지 않습니다.

## 패널의 'M 회랑으로 들어가기' 버튼.
signal corridor_requested
## 패널의 'F 히어로 유역으로' 버튼.
signal hero_requested

const FONT_NAMES := [
	"Apple SD Gothic Neo", "AppleGothic", "Malgun Gothic", "Noto Sans CJK KR",
	"Noto Sans KR", "NanumGothic", "sans-serif",
]
const FONT_SIZE := 15
const PANEL_WIDTH := 380
## 패널 안 글의 너비 (여백과 스크롤 막대 자리 빼고).
const INNER_WIDTH := PANEL_WIDTH - 40
## 범주가 이보다 많으면 범례 색 목록을 스크롤 상자에 넣습니다.
const MANY_CATEGORIES := 12
const CHIP_LIST_HEIGHT := 150
const LEGEND_BAR_HEIGHT := 18
const TICK_FONT_SIZE := FONT_SIZE - 2
const DIM_COLOR := Color(0.72, 0.76, 0.82)
const BAKE_COMMAND := "python -m bpcg.bake.globe --planet <run>/planet --engine"
## 자료 폴더에 globe.json 이 없을 때의 알림.
const MISSING_TEXT := "지구본 자료가 없습니다: " + BAKE_COMMAND + " 로 구우세요"
## globe.json 은 있는데 형식이 틀리거나 파일이 모자랄 때의 알림.
const BROKEN_TEXT := ("지구본 자료를 읽지 못했습니다: 파일이 잘못됐거나 서로 맞지 않습니다. "
		+ BAKE_COMMAND + " 로 다시 구우세요")
const HELP_TEXT := """[왼쪽 끌기] 돌리기    [휠] 확대·축소    [Space] 자동 회전    [F] 히어로 유역으로
[1~9] 레이어 고르기 (나머지는 패널에서 클릭)    [R] 강    [Shift+R] 강 등급 문턱    [K] 호수
[L] 경위선 15°    [G] 그늘·대기 빛 (꾸밈, 끄면 범례 색 그대로)    [ [ ] ] 고도 과장 줄이기·늘리기
[클릭] 그 자리 값 모두 고정    [Esc] 고정 풀기    [Tab] 패널 숨기기    [H] 도움말 숨기기
[M] 회랑으로 들어가기"""
## 그늘·대기 빛 설명 (패널).
const SHADING_NOTE := ("그늘은 비탈만 밝게·어둡게 합니다(왼쪽 위에서 오는 빛). 평평한 곳은 지구본 "
		+ "어디서나 범례 색 그대로이고, 끄면 모든 칸이 범례 색과 같습니다. 지구본 둘레의 푸른 빛은 "
		+ "지구본 바깥에만 그리는 꾸밈이고 자료가 아닙니다.")

var _font: SystemFont
var _globe: GlobeView
var _status: Label
var _pinned_panel: PanelContainer
var _pinned: Label
var _help: Label
var _missing: Label
var _side: VBoxContainer
var _layers_panel: PanelContainer
var _rows: VBoxContainer
var _legend_panel: PanelContainer
var _legend_title: Label
var _legend_bar: TextureRect
var _bar_marker: ColorRect
var _ticks: Control
var _tick_min: Label
var _tick_mid: Label
var _tick_max: Label
var _no_data_chip: HBoxContainer
var _chips: GridContainer
var _chips_scroll: ScrollContainer
var _chips_scrolled: GridContainer
var _area_note: Label
var _description: Label
var _how_to_read: Label
var _field_buttons := {}
var _overlay_buttons := {}
var _river_class_button: Button
## 강 등급 번호 → 범례 줄 (문턱 아래 등급은 흐리게).
var _river_chips := {}
var _grid_button: CheckButton
var _shading_button: CheckButton
var _exaggeration_label: Label


func _ready() -> void:
	_font = SystemFont.new()
	_font.font_names = PackedStringArray(FONT_NAMES)

	var left := VBoxContainer.new()
	left.position = Vector2(16, 12)
	left.mouse_filter = Control.MOUSE_FILTER_IGNORE
	left.add_theme_constant_override("separation", 8)
	_status = _label()
	left.add_child(_status)
	_pinned_panel = _panel()
	_pinned_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_pinned_panel.visible = false
	_pinned = _label(FONT_SIZE - 1)
	_pinned_panel.add_child(_pinned)
	left.add_child(_pinned_panel)
	add_child(left)

	_help = _label()
	_help.text = HELP_TEXT
	_help.anchor_top = 1.0
	_help.anchor_bottom = 1.0
	_help.grow_vertical = Control.GROW_DIRECTION_BEGIN
	_help.offset_left = 16
	_help.offset_top = -16
	_help.offset_bottom = -16
	add_child(_help)

	_side = VBoxContainer.new()
	_side.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_side.anchor_left = 1.0
	_side.anchor_right = 1.0
	_side.anchor_bottom = 1.0
	_side.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	_side.offset_left = -16 - PANEL_WIDTH
	_side.offset_right = -16
	_side.offset_top = 12
	_side.offset_bottom = -12
	_side.add_theme_constant_override("separation", 8)
	add_child(_side)

	_layers_panel = _panel()
	_layers_panel.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_rows = VBoxContainer.new()
	_rows.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_rows.add_theme_constant_override("separation", 4)
	scroll.add_child(_rows)
	_layers_panel.add_child(scroll)
	_side.add_child(_layers_panel)

	_legend_panel = _panel()
	_build_legend()
	_side.add_child(_legend_panel)
	_side.visible = false

	_missing = _label(20)
	_missing.anchor_right = 1.0
	_missing.anchor_bottom = 1.0
	_missing.offset_left = 40
	_missing.offset_right = -40
	_missing.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_missing.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_missing.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_missing.visible = false
	add_child(_missing)


## 지구본에 묶어 레이어 패널과 범례를 만듭니다.
func bind(globe: GlobeView) -> void:
	_globe = globe
	_missing.visible = false
	_side.visible = true
	for child in _rows.get_children():
		_rows.remove_child(child)
		child.queue_free()
	_field_buttons.clear()
	_overlay_buttons.clear()
	_river_chips.clear()
	_river_class_button = null
	var data := globe.data

	var title := _label(FONT_SIZE + 2)
	title.text = str(data.meta.get("title", "행성 지구본"))
	_rows.add_child(title)
	var about := _paragraph(FONT_SIZE - 3)
	about.modulate = DIM_COLOR
	about.text = _about_text(data)
	_rows.add_child(about)

	_rows.add_child(_section("레이어 (버튼을 누르면 그 값으로 지구본을 칠합니다)"))
	var group := ButtonGroup.new()
	var ordered := data.ordered_field_names()
	var current_group := ""
	var flow: HFlowContainer
	for i in ordered.size():
		var name := ordered[i]
		var g := data.group_of(name)
		if g != current_group or flow == null:
			current_group = g
			var group_label := _label(FONT_SIZE - 2)
			group_label.text = g
			group_label.modulate = DIM_COLOR
			_rows.add_child(group_label)
			flow = HFlowContainer.new()
			flow.add_theme_constant_override("h_separation", 4)
			flow.add_theme_constant_override("v_separation", 4)
			flow.custom_minimum_size = Vector2(INNER_WIDTH, 0)
			_rows.add_child(flow)
		var key := "%d  " % (i + 1) if i < 9 else ""
		var button := _button(key + data.label_of(name))
		button.toggle_mode = true
		button.button_group = group
		button.tooltip_text = str(data.field(name).get("description", ""))
		button.pressed.connect(func() -> void: _globe.set_field(name))
		flow.add_child(button)
		_field_buttons[name] = button

	_rows.add_child(HSeparator.new())
	_rows.add_child(_section("겹쳐 보기"))
	for o in data.overlays:
		_add_overlay_rows(o)
	_grid_button = _check("L  경위선 (%d° 간격, 적도·본초 자오선은 굵게)" % int(
			GlobeView.GRID_STEP_DEG))
	_grid_button.toggled.connect(func(on: bool) -> void: _globe.set_grid_visible(on))
	_rows.add_child(_grid_button)
	_shading_button = _check("G  그늘·대기 빛 (꾸밈, 자료 아님)")
	_shading_button.tooltip_text = SHADING_NOTE
	_shading_button.toggled.connect(func(on: bool) -> void: _globe.set_shading_visible(on))
	_rows.add_child(_shading_button)
	var shading_note := _paragraph(FONT_SIZE - 3)
	shading_note.modulate = DIM_COLOR
	shading_note.text = SHADING_NOTE
	_rows.add_child(shading_note)

	var ex_row := HBoxContainer.new()
	ex_row.add_theme_constant_override("separation", 6)
	var down := _button("[  −")
	down.tooltip_text = "고도 과장을 한 단계 줄입니다"
	down.pressed.connect(func() -> void: _globe.step_exaggeration(-1))
	ex_row.add_child(down)
	_exaggeration_label = _label()
	_exaggeration_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_exaggeration_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	ex_row.add_child(_exaggeration_label)
	var up := _button("+  ]")
	up.tooltip_text = "고도 과장을 한 단계 늘립니다"
	up.pressed.connect(func() -> void: _globe.step_exaggeration(1))
	ex_row.add_child(up)
	_rows.add_child(ex_row)
	var ex_note := _paragraph(FONT_SIZE - 3)
	ex_note.modulate = DIM_COLOR
	ex_note.text = ("높이만 과장 배율만큼 늘려 그립니다 (×1 = 실제 비율, 가장 높은 산도 반지름의 "
			+ "0.1 % 남짓). 바다는 해수면 높이로 평평하게 두고 색으로만 깊이를 보입니다.")
	_rows.add_child(ex_note)

	_rows.add_child(HSeparator.new())
	if globe.hero_direction() != Vector3.ZERO:
		var hero := _button("F  히어로 유역으로 날아가기")
		hero.tooltip_text = str(data.hero.get("description", ""))
		hero.pressed.connect(func() -> void: hero_requested.emit())
		_rows.add_child(hero)
	var corridor := _button("M  회랑으로 들어가기 (걷는 장면)")
	corridor.pressed.connect(func() -> void: corridor_requested.emit())
	_rows.add_child(corridor)

	if not globe.field_changed.is_connected(_on_field_changed):
		globe.field_changed.connect(_on_field_changed)
	if not globe.display_changed.is_connected(refresh):
		globe.display_changed.connect(refresh)
	refresh()
	_on_field_changed(globe.active_field)


## 자료가 없거나(missing) 읽지 못했을 때 가운데에 알림을 보이고 패널을 숨깁니다.
func show_missing(reason: String, missing: bool = true) -> void:
	_side.visible = false
	_pinned_panel.visible = false
	_missing.text = (MISSING_TEXT if missing else BROKEN_TEXT) + "\n\n(" + reason + ")"
	_missing.visible = true
	_status.text = ("지구본 자료 없음" if missing else "지구본 자료를 읽지 못함") \
			+ " · M 으로 회랑 장면으로 갈 수 있습니다"


## 버튼 상태와 고도 과장·강 등급 글을 지구본 상태에 맞춥니다 (신호를 다시 내지 않음).
func refresh() -> void:
	if _globe == null:
		return
	for name in _field_buttons:
		var button: Button = _field_buttons[name]
		button.set_pressed_no_signal(name == _globe.active_field)
	for name in _overlay_buttons:
		var check: CheckButton = _overlay_buttons[name]
		check.set_pressed_no_signal(_globe.is_overlay_visible(name))
	if _grid_button != null:
		_grid_button.set_pressed_no_signal(_globe.show_grid)
	if _shading_button != null:
		_shading_button.set_pressed_no_signal(_globe.show_shading)
	if _exaggeration_label != null:
		_exaggeration_label.text = "고도 과장 ×%s" % format_factor(_globe.exaggeration)
	if _river_class_button != null:
		_river_class_button.text = "Shift+R  %d 등급 이상만 그림 (누르면 바뀜)" % (
				_globe.river_min_class)
	for k in _river_chips:
		var chip: Control = _river_chips[k]
		var drawn: bool = _globe.show_rivers and int(k) >= _globe.river_min_class
		chip.modulate = Color(1, 1, 1, 1.0 if drawn else 0.35)


func set_status(text: String) -> void:
	_status.text = text


## 고정한 자리의 값 목록을 보입니다. 빈 글이면 상자를 숨깁니다.
func set_pinned(text: String) -> void:
	_pinned.text = text
	_pinned_panel.visible = not text.is_empty()


func pinned_text() -> String:
	return _pinned.text if _pinned_panel.visible else ""


func toggle_help() -> void:
	_help.visible = not _help.visible


func toggle_panel() -> void:
	if _globe != null and _globe.is_ready():
		_side.visible = not _side.visible


func is_panel_visible() -> bool:
	return _side.visible


func is_missing_visible() -> bool:
	return _missing.visible


func missing_text() -> String:
	return _missing.text


## 범례 제목 (검사용).
func legend_title() -> String:
	return _legend_title.text


## 범례에 보이는 범주 수 (연속 필드면 0, 검사용). 지운 줄은 다음 프레임에 사라지므로 뺍니다.
func legend_chip_count() -> int:
	var n := 0
	for grid in [_chips, _chips_scrolled]:
		for child in grid.get_children():
			if not child.is_queued_for_deletion():
				n += 1
	return n


## 범례 색 막대가 보이는지 (검사용).
func legend_bar_visible() -> bool:
	return _legend_bar.visible


## 범례 막대 아래 글 [최소, 가운데, 최대] (검사용).
func legend_ticks() -> PackedStringArray:
	return PackedStringArray([_tick_min.text, _tick_mid.text, _tick_max.text])


func field_button(name: String) -> Button:
	return _field_buttons.get(name)


func overlay_button(name: String) -> CheckButton:
	return _overlay_buttons.get(name)


func grid_button() -> CheckButton:
	return _grid_button


func shading_button() -> CheckButton:
	return _shading_button


## 범례의 넓이 몫 잣대 글 (보이지 않으면 빈 글, 검사용).
func legend_area_note() -> String:
	return _area_note.text if _area_note.visible else ""


## 마우스 자리 읽기: 위도·경도, 고도, 고른 레이어 값, 그 칸의 강·호수.
func describe_point(dir: Vector3) -> String:
	var data := _globe.data
	var lines: Array[String] = []
	lines.append("위도·경도: " + GlobeData.format_lat_lon(GlobeData.lat_lon(dir)))
	var active := _globe.active_field
	if data.has_field(GlobeData.ELEVATION_FIELD):
		lines.append("%s: %s" % [data.label_of(GlobeData.ELEVATION_FIELD), data.format_value(
				GlobeData.ELEVATION_FIELD, data.value_at(GlobeData.ELEVATION_FIELD, dir))])
	else:
		lines.append("고도: %s m" % GlobeData.format_number(data.corner_elevation_at(dir)))
	if active != GlobeData.ELEVATION_FIELD and data.has_field(active):
		lines.append("%s: %s" % [data.label_of(active), data.format_value(
				active, data.value_at(active, dir))])
	for o in data.overlays:
		var oname := str(o["name"])
		var v := data.overlay_value_at(oname, dir)
		if v != 0 and _globe.is_overlay_visible(oname):
			lines.append(_overlay_line(o, v))
	lines.append("클릭하면 이 자리의 모든 레이어 값을 고정해 봅니다")
	return "\n".join(lines)


## 고정 상자: 그 자리의 모든 레이어 값 (무리별, 이름: 값 단위).
func describe_all(dir: Vector3) -> String:
	var data := _globe.data
	var cell := data.direction_to_cell(dir)
	var lines: Array[String] = []
	lines.append("고정한 자리: " + GlobeData.format_lat_lon(GlobeData.lat_lon(dir))
			+ "  (Esc 로 풀기)")
	lines.append("칸: 면 %d, 행 %d, 열 %d (면당 %d × %d 칸)" % [
		cell["face"], cell["row"], cell["col"], data.face_res, data.face_res])
	var group := ""
	for name in data.ordered_field_names():
		var g := data.group_of(name)
		if g != group:
			group = g
			lines.append("[%s]" % g)
		var v := data.raw_value(name, cell["face"], cell["row"], cell["col"])
		lines.append("  %s: %s" % [data.label_of(name), data.format_value(name, v)])
	if not data.overlays.is_empty():
		lines.append("[겹쳐 보기]")
		for o in data.overlays:
			lines.append("  " + _overlay_line(o, data.overlay_value_at(str(o["name"]), dir)))
	return "\n".join(lines)


## 겹쳐 보기 한 줄: "강: 강 (10~100 m³/s)". 값이 있는데 지금 그리지 않으면 까닭을 덧붙입니다
## (예: 강 등급 문턱 아래). 화면에 없는 것을 있는 것처럼 읽지 않게 하려는 것입니다.
func _overlay_line(o: Dictionary, value: int) -> String:
	var oname := str(o["name"])
	var line := "%s: %s" % [str(o.get("label", oname)), _globe.data.overlay_value_label(oname, value)]
	if value != 0:
		var why := _globe.overlay_hidden_reason(oname, value)
		if not why.is_empty():
			line += " (%s)" % why
	return line


## 1.0 → "1", 2.5 → "2.5".
static func format_factor(x: float) -> String:
	return str(int(x)) if is_equal_approx(x, roundf(x)) else "%.1f" % x


func _add_overlay_rows(o: Dictionary) -> void:
	var data := _globe.data
	var oname := str(o["name"])
	var olabel := str(o.get("label", oname))
	var keys := {GlobeView.OVERLAY_RIVERS: "R", GlobeView.OVERLAY_LAKES: "K"}
	var key: String = keys.get(oname, "")
	var check := _check(olabel if key.is_empty() else "%s  %s" % [key, olabel])
	check.tooltip_text = str(o.get("description", ""))
	check.toggled.connect(func(on: bool) -> void: _globe.set_overlay_visible(oname, on))
	_rows.add_child(check)
	_overlay_buttons[oname] = check
	var how := str(o.get("how_to_read", ""))
	if oname == GlobeView.OVERLAY_RIVERS:
		how += (" L0 칸이 커서 육지 대부분이 강 문턱을 넘으므로, 처음에는 큰 강(%d 등급)부터 "
				+ "그립니다.") % GlobeView.DEFAULT_RIVER_MIN_CLASS
	if not how.is_empty():
		var note := _paragraph(FONT_SIZE - 3)
		note.modulate = DIM_COLOR
		note.text = how.strip_edges()
		_rows.add_child(note)
	if oname == GlobeView.OVERLAY_RIVERS:
		_river_class_button = _button("")
		_river_class_button.tooltip_text = "그릴 강의 가장 낮은 유량 등급을 바꿉니다 (Shift+R)"
		_river_class_button.pressed.connect(_globe.cycle_river_min_class)
		_rows.add_child(_river_class_button)
		for cat in o.get("categories", []):
			var id := int(cat.get("id", 0))
			if id <= 0:
				continue
			var chip := _chip(data.overlay_value_color(oname, id, GlobeView.RIVER_FALLBACK_COLOR),
					"%d 등급: %s" % [id, data.overlay_value_label(oname, id)], INNER_WIDTH - 20)
			_rows.add_child(chip)
			_river_chips[id] = chip
	else:
		_rows.add_child(_chip(data.overlay_value_color(oname, 1, GlobeView.LAKE_FALLBACK_COLOR),
				data.overlay_value_label(oname, 1), INNER_WIDTH - 20))


func _on_field_changed(name: String) -> void:
	refresh()
	if _globe == null or not _globe.data.has_field(name):
		return
	var data := _globe.data
	var unit := data.display_unit_of(name)
	var title := data.label_of(name)
	var extra: Array[String] = []
	if not unit.is_empty():
		extra.append(unit)
	if data.is_log(name):
		extra.append("로그 눈금")
	if data.is_categorical(name):
		extra.append("범주")
	if not extra.is_empty():
		title += " (" + ", ".join(extra) + ")"
	_legend_title.text = title

	for grid in [_chips, _chips_scrolled]:
		for child in grid.get_children():
			grid.remove_child(child)
			child.queue_free()
	if data.is_categorical(name):
		_legend_bar.visible = false
		_ticks.visible = false
		_no_data_chip.visible = false
		var cats := data.categories(name)
		var many := cats.size() > MANY_CATEGORIES
		var grid := _chips_scrolled if many else _chips
		for cat in cats:
			var id := int(cat.get("id", -1))
			var text := data.category_label(name, id)
			var frac := data.area_fraction(name, id)
			if not is_nan(frac):
				text += " (%s)" % _percent(frac)
			grid.add_child(_chip(data.category_color(name, id), text, (INNER_WIDTH - 12) / 2.0))
		_chips.visible = not many
		_chips_scroll.visible = many
		_area_note.text = data.area_fraction_note(name)
		_area_note.visible = not _area_note.text.is_empty()
	else:
		_chips.visible = false
		_chips_scroll.visible = false
		_area_note.visible = false
		_legend_bar.visible = true
		_ticks.visible = true
		_legend_bar.texture = ImageTexture.create_from_image(data.legend_image(name, 256))
		_set_ticks(name)
		var nan_label: Variant = data.field(name).get("nan_label")
		_no_data_chip.visible = nan_label != null
		if nan_label != null:
			(_no_data_chip.get_child(0) as ColorRect).color = data.no_data_color
			(_no_data_chip.get_child(1) as Label).text = data.no_data_text(name)
	var desc := str(data.field(name).get("description", ""))
	var how := str(data.field(name).get("how_to_read", ""))
	_description.text = "무엇인가: " + (desc if not desc.is_empty() else "(설명 없음)")
	_how_to_read.text = "읽는 법: " + (how if not how.is_empty() else "(설명 없음)")


## 막대 아래 글: 왼쪽 끝 = 최소, 오른쪽 끝 = 최대, 가운데 = 0 (범위가 0 을 걸치고 로그가 아니면,
## 그 자리에 표시선) 또는 가운데 값. 자료가 막대 범위 밖까지 있으면 '이하'·'이상'을 붙입니다.
func _set_ticks(name: String) -> void:
	var data := _globe.data
	var r := data.value_range(name)
	var span := r.y - r.x
	var real := data.data_range(name)
	var eps := absf(span) * 1e-6
	_tick_min.text = data.format_value(name, r.x)
	if not is_nan(real.x) and real.x < r.x - eps:
		_tick_min.text += " 이하"
	_tick_max.text = data.format_value(name, r.y)
	if not is_nan(real.y) and real.y > r.y + eps:
		_tick_max.text += " 이상"
	var mid := 0.5 * (r.x + r.y)
	if not data.is_log(name) and r.x < 0.0 and r.y > 0.0:
		mid = 0.0
	_tick_mid.text = data.format_value(name, mid)
	var f := 0.5 if span <= 0.0 else clampf((mid - r.x) / span, 0.0, 1.0)

	var width := float(INNER_WIDTH)
	var line_h := _text_size("가").y
	var w_min := _text_size(_tick_min.text).x
	var w_mid := _text_size(_tick_mid.text).x
	var w_max := _text_size(_tick_max.text).x
	_tick_min.position = Vector2.ZERO
	_tick_max.position = Vector2(width - w_max, 0.0)
	var x := f * width - 0.5 * w_mid
	var rows := 1
	if x < w_min + 8.0 or x + w_mid > width - w_max - 8.0:
		# 양 끝 글과 겹치면 둘째 줄에 둡니다.
		rows = 2
		x = clampf(x, 0.0, maxf(width - w_mid, 0.0))
	_tick_mid.position = Vector2(x, line_h * float(rows - 1))
	_ticks.custom_minimum_size = Vector2(width, line_h * float(rows) + 2.0)
	_bar_marker.position = Vector2(f * width - 1.0, 0.0)


func _build_legend() -> void:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 6)
	var caption := _label(FONT_SIZE - 2)
	caption.text = "범례 (지금 칠한 레이어)"
	caption.modulate = DIM_COLOR
	box.add_child(caption)
	_legend_title = _label(FONT_SIZE + 1)
	box.add_child(_legend_title)
	_legend_bar = TextureRect.new()
	_legend_bar.custom_minimum_size = Vector2(INNER_WIDTH, LEGEND_BAR_HEIGHT)
	_legend_bar.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_legend_bar.stretch_mode = TextureRect.STRETCH_SCALE
	_legend_bar.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_bar_marker = ColorRect.new()
	_bar_marker.color = Color(1, 1, 1, 0.9)
	_bar_marker.size = Vector2(2, LEGEND_BAR_HEIGHT)
	_bar_marker.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_legend_bar.add_child(_bar_marker)
	box.add_child(_legend_bar)
	_ticks = Control.new()
	_ticks.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_tick_min = _label(TICK_FONT_SIZE)
	_tick_mid = _label(TICK_FONT_SIZE)
	_tick_max = _label(TICK_FONT_SIZE)
	for tick in [_tick_min, _tick_mid, _tick_max]:
		_ticks.add_child(tick)
	box.add_child(_ticks)
	_no_data_chip = _chip(GlobeData.NO_DATA_COLOR, GlobeData.NO_DATA_TEXT, INNER_WIDTH - 20)
	box.add_child(_no_data_chip)
	_chips = GridContainer.new()
	_chips.columns = 2
	box.add_child(_chips)
	_chips_scroll = ScrollContainer.new()
	_chips_scroll.custom_minimum_size = Vector2(INNER_WIDTH, CHIP_LIST_HEIGHT)
	_chips_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_chips_scrolled = GridContainer.new()
	_chips_scrolled.columns = 2
	_chips_scroll.add_child(_chips_scrolled)
	box.add_child(_chips_scroll)
	_area_note = _paragraph(FONT_SIZE - 3)
	_area_note.modulate = DIM_COLOR
	_area_note.visible = false
	box.add_child(_area_note)
	_description = _paragraph(FONT_SIZE - 2)
	box.add_child(_description)
	_how_to_read = _paragraph(FONT_SIZE - 2)
	box.add_child(_how_to_read)
	_legend_panel.add_child(box)


func _about_text(data: GlobeData) -> String:
	var parts: Array[String] = []
	var desc := str(data.meta.get("description", ""))
	if not desc.is_empty():
		parts.append(desc)
	# 칸 크기는 globe.json 의 설명과 같은 잣대(면 가운데 칸 한 변 = 2πR / 4N)입니다.
	var cell_km := data.radius_m * (PI / 2.0) / float(data.face_res) / 1000.0
	var facts: Array[String] = []
	if data.meta.get("seed") != null:
		facts.append("시드 %s" % str(data.meta["seed"]))
	facts.append("면당 %d × %d 칸 (면 가운데 칸 한 변 약 %.1f km)" % [
		data.face_res, data.face_res, cell_km])
	facts.append("반지름 %s km" % GlobeData.format_number(data.radius_m / 1000.0))
	parts.append(" · ".join(facts))
	return "\n".join(parts)


func _section(text: String) -> Label:
	var label := _label(FONT_SIZE - 1)
	label.text = text
	return label


func _chip(color: Color, text: String, width: float) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 6)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var swatch := ColorRect.new()
	swatch.color = color
	swatch.custom_minimum_size = Vector2(14, 14)
	swatch.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	swatch.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(swatch)
	var label := _label(FONT_SIZE - 2)
	label.text = text
	label.custom_minimum_size = Vector2(maxf(width - 20.0, 40.0), 0)
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	return row


func _text_size(text: String) -> Vector2:
	return _font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, TICK_FONT_SIZE)


static func _percent(frac: float) -> String:
	var p := frac * 100.0
	if p >= 10.0:
		return "%.0f %%" % p
	if p >= 0.1:
		return "%.1f %%" % p
	return "0.1 % 미만" if p > 0.0 else "0 %"


func _panel() -> PanelContainer:
	var panel := PanelContainer.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.06, 0.07, 0.09, 0.80)
	style.set_corner_radius_all(6)
	style.set_content_margin_all(10)
	panel.add_theme_stylebox_override("panel", style)
	return panel


func _paragraph(font_size: int) -> Label:
	var label := _label(font_size)
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.custom_minimum_size = Vector2(INNER_WIDTH, 0)
	return label


func _label(font_size: int = FONT_SIZE) -> Label:
	var label := Label.new()
	label.add_theme_font_override("font", _font)
	label.add_theme_font_size_override("font_size", font_size)
	label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.85))
	label.add_theme_constant_override("outline_size", 5)
	label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	return label


func _button(text: String) -> Button:
	var button := Button.new()
	button.text = text
	button.focus_mode = Control.FOCUS_NONE
	button.add_theme_font_override("font", _font)
	button.add_theme_font_size_override("font_size", FONT_SIZE - 1)
	return button


func _check(text: String) -> CheckButton:
	var check := CheckButton.new()
	check.text = text
	check.focus_mode = Control.FOCUS_NONE
	check.add_theme_font_override("font", _font)
	check.add_theme_font_size_override("font_size", FONT_SIZE - 1)
	return check
