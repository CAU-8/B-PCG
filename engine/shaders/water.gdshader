// 수면 셰이더 (호수·강 수면, 지하수면). 반투명하고, 자르는 면의 한쪽을 버립니다.
shader_type spatial;
render_mode cull_disabled, specular_schlick_ggx;

#include "res://shaders/section.gdshaderinc"

uniform vec4 water_color : source_color = vec4(0.10, 0.30, 0.42, 0.78);
uniform float roughness : hint_range(0.0, 1.0) = 0.06;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (section_cut(world_pos)) {
		discard;
	}
	ALBEDO = water_color.rgb;
	ALPHA = water_color.a;
	ROUGHNESS = roughness;
	SPECULAR = 0.5;
}
