// 동굴 벽 셰이더. 꼭짓점 색(COLOR_0)의 알파 = 재질 번호 / 255 로 벽 뒤 암석 색을 칠합니다.
//
// 재질 번호는 보간하지 않습니다 (flat). 보간하면 석회암(3)과 흙(11) 사이 번호의 색 줄이 생깁니다.
// 굽기는 법선이 빈 곳(동굴 안)을 보게 씁니다. 그래서 뒷면을 버리면 안에서는 벽이 보이고,
// 바깥(땅속, 단면)에서는 가까운 벽이 사라져 동굴 속이 들여다보입니다.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/section.gdshaderinc"

uniform sampler2D palette : source_color, filter_nearest, repeat_disable;
// false 면 꼭짓점 색 RGB(sRGB)를 그대로 씁니다 (재질 부피가 없을 때).
uniform bool use_palette = false;

varying vec3 world_pos;
varying flat float rock_id;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	rock_id = floor(COLOR.a * 255.0 + 0.5);
}

void fragment() {
	if (section_cut(world_pos)) {
		discard;
	}
	vec3 c;
	if (use_palette) {
		c = texture(palette, vec2((rock_id + 0.5) / 256.0, 0.5)).rgb;
	} else {
		c = pow(COLOR.rgb, vec3(2.2));
	}
	ALBEDO = c * 0.9;
	ROUGHNESS = 0.8;
}
