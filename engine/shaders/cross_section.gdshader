// 지형 셰이더. 자르는 면의 한쪽을 버리고, 남은 쪽을 칠합니다.
//
// 자르는 면과 사각형: section.gdshaderinc.
// color_mode 0: 높이에 따른 층 무늬 (구운 묶음이 없을 때의 표본 지형, 자리표시).
// color_mode 1: 자연색. 재질 부피가 있으면 맨 위 층으로 칠합니다 (흙 = 풀, 충적층 = 모래빛,
//               드러난 암반 = 그 암석 색). 없으면 경사와 고도로 칠합니다 (주변 25 m 지형).
//               물(254)과 공기(255)는 경사로 칠합니다 (수면은 수면 메시가 보여 줍니다).
// color_mode 2: 지질도. 흙·충적층 아래 10 m 안의 첫 기반암 색.
// 동굴 입구 구멍: cave_mouth (지표 점의 동굴 거리 d_cave, 굽기) 가 음수인 곳을 버립니다.
shader_type spatial;
render_mode cull_disabled;

#include "res://shaders/section.gdshaderinc"
#include "res://shaders/strata.gdshaderinc"

// 이 사각형 (x_min, z_min, x_max, z_max) 안은 그리지 않습니다. 넓이가 0 이면 끕니다.
// 주변 25 m 지형이 고운 회랑 지형 자리를 비울 때 씁니다.
uniform vec4 hole_rect = vec4(0.0);
uniform int color_mode = 0;
// 엔진 Y + y_offset_m = 해발 고도 (m)
uniform float y_offset_m = 0.0;
// cave_mouth < 0 인 곳(지표가 동굴 빈 곳 안)에 구멍을 뚫어 동굴 입구를 보입니다.
uniform bool open_entrances = false;
uniform sampler2D cave_mouth : filter_linear, repeat_disable;
// cave_mouth 격자: 0번 표본의 (X, Z), 간격 (m), (열 수, 행 수). 크기 0 이면 없음.
uniform vec2 mouth_origin = vec2(0.0);
uniform float mouth_spacing_m = 1.0;
uniform vec2 mouth_size = vec2(0.0);
// 층 무늬 한 장의 두께 (m).
uniform float strata_thickness_m = 20.0;
uniform vec3 color_low : source_color = vec3(0.45, 0.36, 0.26);
uniform vec3 color_high : source_color = vec3(0.78, 0.70, 0.55);
// 층마다 밝기를 조금씩 흔드는 정도 (0 이면 두 색만 번갈아 씁니다).
uniform float band_jitter : hint_range(0.0, 0.5) = 0.15;

const vec3 GRASS = vec3(0.095, 0.17, 0.04);   // sRGB (0.34, 0.45, 0.22) 의 선형값
const vec3 BARE = vec3(0.21, 0.19, 0.16);     // sRGB (0.50, 0.48, 0.44)
const vec3 SNOW = vec3(0.85, 0.87, 0.91);

varying vec3 world_pos;
varying vec3 world_normal;

float band_hash(float n) {
	return fract(sin(n * 12.9898) * 43758.5453);
}

// 재질 부피가 없는 곳: 경사와 고도로 칠합니다. up = 법선의 Y 성분.
vec3 natural_color(float elevation_m, float up) {
	float steep = smoothstep(0.82, 0.68, up);
	vec3 c = mix(GRASS, BARE, steep);
	return mix(c, SNOW, smoothstep(3600.0, 4000.0, elevation_m) * (1.0 - 0.6 * steep));
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 p = world_pos;
	if (hole_rect.z > hole_rect.x && in_rect(hole_rect, p.xz)) {
		discard;
	}
	if (section_cut(p)) {
		discard;
	}
	if (open_entrances && mouth_size.x > 0.0) {
		vec2 g = (p.xz - mouth_origin) / mouth_spacing_m;
		if (g.x >= 0.0 && g.y >= 0.0 && g.x <= mouth_size.x - 1.0 && g.y <= mouth_size.y - 1.0
				&& texture(cave_mouth, (g + 0.5) / mouth_size).r < 0.0) {
			discard;
		}
	}
	vec3 base;
	if (color_mode == 0) {
		float band = floor(p.y / strata_thickness_m);
		float t = mod(band, 2.0);
		float shade = 1.0 + (band_hash(band) - 0.5) * 2.0 * band_jitter;
		base = clamp(mix(color_low, color_high, t) * shade, 0.0, 1.0);
	} else {
		float up = abs(normalize(world_normal).y);
		base = natural_color(p.y + y_offset_m, up);
		if (strata_covers(p.xz)) {
			float layer0 = max((strata_top_y(p.xz) - p.y) / strata_spacing.y, 0.0);
			float id0 = strata_id(p.xz, layer0);
			// 흙·충적층 아래 10 m (5층) 안의 첫 기반암 (1..10)
			float bed = -1.0;
			for (int k = 0; k < 5; k++) {
				float id = strata_id(p.xz, layer0 + float(k));
				if (id > 0.5 && id < 10.5) {
					bed = id;
					break;
				}
			}
			if (color_mode == 2) {
				base = strata_color(bed >= 0.0 ? bed : id0);
			} else if (id0 > 10.5 && id0 < 11.5) {
				base = mix(GRASS, BARE, smoothstep(0.80, 0.60, up) * 0.6);
			} else if (id0 < 0.5) {
				base = mix(GRASS, strata_color(0.0), 0.55);
			} else if (id0 < 10.5) {
				base = strata_color(id0) * 0.8;
			}
		}
	}
	ALBEDO = base;
	ROUGHNESS = 0.92;
}
