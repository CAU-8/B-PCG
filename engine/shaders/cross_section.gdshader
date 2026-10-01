// 자르는 면(단면) 셰이더. 면의 한쪽을 버리고, 남은 쪽은 높이에 따른 층 무늬로 칠합니다.
//
// 자르는 면: dot(world_pos, plane_normal) = plane_offset_m (전역 좌표, m).
// 면의 법선 쪽(dot > plane_offset_m)을 버립니다. 버리지 않으려면 plane_offset_m 을 아주 크게 둡니다.
// 층 무늬는 자리표시용입니다. 나중에 땅속 지층 3D 텍스처(sampler3D)로 바꿉니다.
shader_type spatial;
render_mode cull_disabled;

// 자르는 면의 법선 (전역 좌표). 길이는 셰이더가 1 로 맞춥니다.
uniform vec3 plane_normal = vec3(1.0, 0.0, 0.0);
// 원점에서 자르는 면까지 법선 방향 거리 (m).
uniform float plane_offset_m = 100000.0;
// 층 한 장의 두께 (m).
uniform float strata_thickness_m = 20.0;
uniform vec3 color_low : source_color = vec3(0.45, 0.36, 0.26);
uniform vec3 color_high : source_color = vec3(0.78, 0.70, 0.55);
// 층마다 밝기를 조금씩 흔드는 정도 (0 이면 두 색만 번갈아 씁니다).
uniform float band_jitter : hint_range(0.0, 0.5) = 0.15;

varying vec3 world_pos;

float band_hash(float n) {
	return fract(sin(n * 12.9898) * 43758.5453);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 n = normalize(plane_normal);
	if (dot(world_pos, n) > plane_offset_m) {
		discard;
	}
	float band = floor(world_pos.y / strata_thickness_m);
	float t = mod(band, 2.0);
	vec3 base = mix(color_low, color_high, t);
	float shade = 1.0 + (band_hash(band) - 0.5) * 2.0 * band_jitter;
	ALBEDO = clamp(base * shade, 0.0, 1.0);
	ROUGHNESS = 0.9;
}
