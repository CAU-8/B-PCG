// 단면 판 셰이더. 자르는 면 위에 재질 부피를 칠합니다.
//
// 지표 위는 버리고, 동굴(공기)도 버려 뒤의 동굴 메시가 보이게 합니다.
// 지하수면 아래는 파랗게 물들이고, 지하수면과 지표는 선으로 긋습니다.
// 구운 깊이(strata_size.y · dy)보다 깊은 곳은 회색 빗금입니다.
// 지표에서 10 m 마다 가는 선, 50 m 마다 굵은 선을 그어 깊이를 읽게 합니다.
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://shaders/section.gdshaderinc"
#include "res://shaders/strata.gdshaderinc"

// 회랑 지표와 지하수면 높이맵 (값 + surface_origin.y = 엔진 Y, 같은 격자)
uniform sampler2D surface_height : filter_linear, repeat_disable;
uniform sampler2D water_table : filter_linear, repeat_disable;
uniform vec3 surface_origin = vec3(0.0);
uniform float surface_spacing_m = 2.0;
// (열 수, 행 수)
uniform vec2 surface_size = vec2(1.0);

const vec3 SATURATED_TINT = vec3(0.05, 0.2, 0.6);
const vec3 WATER_TABLE_LINE = vec3(0.25, 0.75, 1.0);
const vec3 SURFACE_LINE = vec3(0.08, 0.08, 0.06);
const float DEPTH_TICK_M = 10.0;
const float DEPTH_MAJOR_M = 50.0;

varying vec3 world_pos;

float grid_y(sampler2D tex, vec2 xz) {
	vec2 uv = ((xz - surface_origin.xz) / surface_spacing_m + 0.5) / surface_size;
	return texture(tex, uv).r + surface_origin.y;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 p = world_pos;
	if (!in_rect(section_rect, p.xz)) {
		discard;
	}
	float surface = grid_y(surface_height, p.xz);
	if (p.y > surface) {
		discard;
	}
	float layer = max((strata_top_y(p.xz) - p.y) / strata_spacing.y, 0.0);
	vec3 c;
	if (layer >= strata_size.y) {
		float hatch = step(0.5, fract((p.y + p.x + p.z) / 6.0));
		c = mix(vec3(0.10), vec3(0.16), hatch);
	} else {
		float id = strata_id(p.xz, layer);
		if (id > 254.5) {
			discard;
		}
		c = strata_color(id);
	}
	float line_m = max(fwidth(p.y) * 1.5, 0.15);
	float wt = grid_y(water_table, p.xz);
	if (p.y < wt) {
		c = mix(c, SATURATED_TINT, 0.22);
	}
	if (abs(p.y - wt) < line_m) {
		c = WATER_TABLE_LINE;
	}
	float depth = surface - p.y;
	float tick = abs(depth - DEPTH_TICK_M * floor(depth / DEPTH_TICK_M + 0.5));
	float major = abs(depth - DEPTH_MAJOR_M * floor(depth / DEPTH_MAJOR_M + 0.5));
	if (major < line_m) {
		c *= 0.55;
	} else if (tick < line_m * 0.5) {
		c *= 0.8;
	}
	if (depth < line_m * 1.5) {
		c = SURFACE_LINE;
	}
	ALBEDO = c;
}
