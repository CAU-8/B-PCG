class_name Hud
extends CanvasLayer
## 화면 위 정보와 레이어 패널.
##
## 왼쪽 위: 지금 상태 (모드, 속력, 위치, 발밑 암석, 단면). 왼쪽 아래: 조작 도움말 (H 로 숨김).[br]
## 오른쪽: 레이어 패널. 줄마다 켜기·끄기 버튼과 '만 보기' 버튼이 있고, 아래에 '모두 보기',
## 색 바꾸기, 손전등 버튼이 있습니다 (Tab 으로 숨김). 버튼을 누르려면 Esc 로 마우스를 놓습니다.
## 버튼은 키보드 초점을 받지 않아 Space 같은 키가 버튼을 누르지 않습니다.

## 패널의 '색'·'손전등'·'지구본' 버튼을 누를 때 알립니다. name: "color_mode", "lamp", "globe".
signal action_requested(name: String)

const FONT_NAMES := [
	"Apple SD Gothic Neo", "AppleGothic", "Malgun Gothic", "Noto Sans CJK KR",
	"Noto Sans KR", "NanumGothic", "sans-serif",
]
const FONT_SIZE := 15
const HELP_TEXT := """[V] 날기(노클립) ↔ 걷기    [WASD] 이동    [Space·E] 위    [Q·Ctrl] 아래
[Shift] 빠르게    [휠] 나는 속력    [클릭] 마우스로 둘러보기    [Esc] 마우스 놓기
{layer_keys}
[X] 단면 켜기·끄기 (눈앞에 자르는 면)    [ [ ] ] 단면 당기기·밀기
[T] 다음 동굴 입구로    [Shift+T] 이전 입구    [G] 색: 자연색 ↔ 지질도    [F] 손전등
[M] 지구본 (행성 전체)    [Tab] 레이어 패널 숨기기    [H] 도움말 숨기기"""

var _font: SystemFont
var _status: Label
var _help: Label
var _panel: PanelContainer
var _rows: VBoxContainer
var _layers: BakedLayers
var _toggles := {}
var _color_button: Button
var _lamp_button: Button


func _ready() -> void:
	_font = SystemFont.new()
	_font.font_names = PackedStringArray(FONT_NAMES)

	_status = _label()
	_status.position = Vector2(16, 12)
	add_child(_status)

	_help = _label()
	_help.text = HELP_TEXT.format({"layer_keys": "[1~7] 레이어 켜기·끄기    [0] 모두 보기"})
	_help.anchor_top = 1.0
	_help.anchor_bottom = 1.0
	_help.grow_vertical = Control.GROW_DIRECTION_BEGIN
	_help.offset_left = 16
	_help.offset_top = -16
	_help.offset_bottom = -16
	add_child(_help)

	_panel = PanelContainer.new()
	_panel.anchor_left = 1.0
	_panel.anchor_right = 1.0
	_panel.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	_panel.offset_left = -16
	_panel.offset_right = -16
	_panel.offset_top = 12
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.06, 0.07, 0.09, 0.78)
	style.set_corner_radius_all(6)
	style.set_content_margin_all(10)
	_panel.add_theme_stylebox_override("panel", style)
	_rows = VBoxContainer.new()
	_rows.add_theme_constant_override("separation", 4)
	_panel.add_child(_rows)
	add_child(_panel)


## 레이어 패널을 만듭니다. 레이어가 바뀌면 버튼 상태를 다시 맞춥니다.
func bind_layers(layers: BakedLayers) -> void:
	_layers = layers
	for child in _rows.get_children():
		child.queue_free()
	_toggles.clear()
	var title := _label()
	title.text = "레이어"
	_rows.add_child(title)
	var index := 1
	for id in layers.layer_ids():
		var row := HBoxContainer.new()
		row.add_theme_constant_override("separation", 6)
		var toggle := _button("%d  %s" % [index, layers.layer_label(id)])
		toggle.toggle_mode = true
		toggle.custom_minimum_size = Vector2(200, 0)
		toggle.alignment = HORIZONTAL_ALIGNMENT_LEFT
		toggle.tooltip_text = layers.layer_hint(id)
		toggle.toggled.connect(func(on: bool) -> void: layers.set_layer_visible(id, on))
		row.add_child(toggle)
		_toggles[id] = toggle
		if layers.can_solo(id):
			var solo := _button("만 보기")
			solo.tooltip_text = "이 레이어만 보이고 나머지는 숨깁니다 (Shift+%d)" % index
			solo.pressed.connect(func() -> void: layers.solo(id))
			row.add_child(solo)
		_rows.add_child(row)
		index += 1
	_help.text = HELP_TEXT.format({"layer_keys": _layer_keys_line(layers)})
	var all := _button("0  모두 보기")
	all.pressed.connect(layers.show_all)
	_rows.add_child(all)
	_color_button = _button("")
	_color_button.pressed.connect(func() -> void: action_requested.emit("color_mode"))
	_rows.add_child(_color_button)
	_lamp_button = _button("")
	_lamp_button.pressed.connect(func() -> void: action_requested.emit("lamp"))
	_rows.add_child(_lamp_button)
	var globe := _button("M  지구본 (행성 전체)")
	globe.tooltip_text = "행성 전체를 지구본으로 봅니다. 지구본에서 M 을 누르면 돌아옵니다"
	globe.pressed.connect(func() -> void: action_requested.emit("globe"))
	_rows.add_child(globe)
	layers.layers_changed.connect(refresh_layers)
	refresh_layers()


## 도움말의 레이어 키 줄. 번호는 지금 있는 레이어 순서(layer_ids)를 따릅니다.
func _layer_keys_line(layers: BakedLayers) -> String:
	var ids := layers.layer_ids()
	var solo: Array[int] = []
	var extra: Array[String] = []
	for i in ids.size():
		if layers.can_solo(ids[i]):
			solo.append(i + 1)
		else:
			extra.append("%d = %s" % [i + 1, layers.layer_label(ids[i])])
	var line := "[1~%d] 레이어 켜기·끄기" % ids.size()
	if not extra.is_empty():
		line += " (%s)" % ", ".join(extra)
	if not solo.is_empty():
		line += "    [Shift+%s] 그 레이어만 보기" % (
				str(solo[0]) if solo.size() == 1 else "%d~%d" % [solo[0], solo[-1]])
	return line + "    [0] 모두 보기"


## 버튼 눌림 상태를 레이어 상태에 맞춥니다 (신호를 다시 내지 않음).
func refresh_layers() -> void:
	if _layers == null:
		return
	for id in _toggles:
		var toggle: Button = _toggles[id]
		toggle.set_pressed_no_signal(_layers.is_layer_visible(id))


## '색' 버튼과 '손전등' 버튼의 글자.
func set_option_labels(color_label: String, lamp_on: bool) -> void:
	if _color_button != null:
		_color_button.text = "G  색: " + color_label
	if _lamp_button != null:
		_lamp_button.text = "F  손전등: " + ("켜짐" if lamp_on else "꺼짐")


func set_status(text: String) -> void:
	_status.text = text


func toggle_help() -> void:
	_help.visible = not _help.visible


func toggle_panel() -> void:
	_panel.visible = not _panel.visible


## 레이어 이름 → 켜기·끄기 버튼 (검사용).
func layer_button(id: String) -> Button:
	return _toggles.get(id)


func _label() -> Label:
	var label := Label.new()
	label.add_theme_font_override("font", _font)
	label.add_theme_font_size_override("font_size", FONT_SIZE)
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
