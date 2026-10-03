// 지구본 면 하나 (globe.gd 가 면마다 재질을 하나씩 씁니다).
//
// 꼭짓점: 메시는 단위 구 위의 모서리 방향 (N+1)² 개입니다. UV = (열/N, 행/N).
// vertex() 에서 그 모서리의 고도(corner_elevation, m)를 읽어 반지름 방향으로 밀어 냅니다.
//   r = 1 + exaggeration · max(고도, 해수면) / radius_m
// 바다는 해수면 높이로 평평하게 두고, 색으로만 수심을 보여 줍니다.
//
// 색: 고른 필드의 RGBA 텍스처(칸 하나 = 텍셀 하나). 범주 필드는 가장 가까운 텍셀입니다. 연속 필드는
// 이웃 텍셀 넷을 직접 읽어 선형 보간하되, 넷의 알파(GlobeData 가 넣는 '종류': 255 = 값, 128 = 색이
// 끊기는 값(color_break) 아래, 0 = 값 없음)가 하나라도 다르면 가장 가까운 텍셀을 씁니다. 그래서
// 값 없는 회색이 이웃 색에 번지거나 해안에서 얕은 바다 파랑과 낮은 땅 초록이 섞이는 등, 범례에
// 없는 색이 생기지 않습니다.
// 그 위에 강(유량 등급별 파랑, river_min_class 등급부터), 호수(하늘색), 15° 경위선을 겹칩니다.
//
// 그늘(shade_relief): 기복만 보이게 합니다. 화면 공간 미분(dFdx, dFdy)으로 구한 면 법선 n_face 와
// 매끈한 구의 법선 n_sphere 가 빛을 받는 정도의 차이만 밝기로 바꿉니다.
//   lit = clamp(1 + relief_shading · (n_face·l − n_sphere·l), 1 − relief_shading_limit, 1 + …)
// 평평한 곳(바다, 평지)은 지구본의 어느 자리든 lit = 1 이라 화면 색이 범례 색과 같습니다.
// 끄면(G 키) 모든 칸이 범례 색 그대로입니다. 범례 색과 맞도록 Godot 조명은 쓰지 않습니다(unshaded).
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque, shadows_disabled;

// 모서리 고도 (m), (N+1) × (N+1), 텍셀 (열, 행).
uniform sampler2D corner_elevation : filter_nearest, repeat_disable;
// 고른 필드의 색, N × N. 보간은 fragment() 에서 직접 합니다 (알파 = 텍셀의 종류).
uniform sampler2D field_color : source_color, filter_nearest, repeat_disable;
// 겹쳐 보기: R = 강 유량 등급 (바이트 값 0~7), G = 호수 (0, 1).
uniform sampler2D overlay : filter_nearest, repeat_disable;

uniform bool categorical = false;
uniform int face_res = 256;
uniform float radius_m = 6371000.0;
uniform float sea_level_m = 0.0;
uniform float exaggeration = 20.0;

uniform bool show_rivers = true;
uniform bool show_lakes = true;
uniform bool show_grid = true;
uniform float grid_step_deg = 15.0;
uniform float grid_opacity = 0.35;
// 강 등급별 색 (선형 색 공간, globe.gd 가 sRGB 에서 바꿔 넣음). 0 번은 쓰지 않습니다.
uniform vec4 river_colors[8];
// 이 등급 이상의 강만 그립니다.
uniform int river_min_class = 3;
uniform vec4 lake_color : source_color = vec4(0.35, 0.78, 0.98, 1.0);
uniform vec4 grid_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);

// 기복 그늘. 빛 방향은 카메라 좌표(표면에서 빛 쪽)로, 왼쪽 위 앞에서 비춥니다(지도의 북서광).
uniform bool shade_relief = true;
uniform vec3 light_dir_view = vec3(-0.45, 0.55, 0.70);
// 비탈 하나가 바꾸는 밝기의 세기와 그 한계 (1 ± 한계).
uniform float relief_shading = 1.5;
uniform float relief_shading_limit = 0.4;

// 메시의 단위 방향 (모델 좌표). 경위선 계산에 씁니다.
varying vec3 v_dir;
// 매끈한 구의 법선 (카메라 좌표).
varying vec3 v_sphere_normal;

void vertex() {
	ivec2 texel = ivec2(round(UV * float(face_res)));
	float elevation_m = texelFetch(corner_elevation, texel, 0).r;
	float shown_m = max(elevation_m, sea_level_m);
	vec3 dir = normalize(VERTEX);
	v_dir = dir;
	v_sphere_normal = MODELVIEW_NORMAL_MATRIX * dir;
	VERTEX = dir * (1.0 + exaggeration * shown_m / radius_m);
}

// 가장 가까운 격자선까지 거리를 픽셀 너비로 나눠 0~1 선 세기로 바꿉니다.
float grid_line(float deg, float width_deg, float step_deg) {
	float dist = abs(fract(deg / step_deg + 0.5) - 0.5) * step_deg;
	return 1.0 - smoothstep(0.5 * width_deg, 1.5 * width_deg, dist);
}

// 필드 색: 범주는 가장 가까운 텍셀, 연속 값은 종류(알파)가 같은 텍셀끼리만 선형 보간.
vec3 field_rgb(vec2 uv) {
	ivec2 size = textureSize(field_color, 0);
	ivec2 last = size - ivec2(1);
	vec2 st = uv * vec2(size);
	ivec2 nearest = clamp(ivec2(floor(st)), ivec2(0), last);
	vec4 c_near = texelFetch(field_color, nearest, 0);
	if (categorical) {
		return c_near.rgb;
	}
	vec2 p = st - 0.5;
	ivec2 i0 = ivec2(floor(p));
	vec2 f = p - floor(p);
	vec4 c00 = texelFetch(field_color, clamp(i0, ivec2(0), last), 0);
	vec4 c10 = texelFetch(field_color, clamp(i0 + ivec2(1, 0), ivec2(0), last), 0);
	vec4 c01 = texelFetch(field_color, clamp(i0 + ivec2(0, 1), ivec2(0), last), 0);
	vec4 c11 = texelFetch(field_color, clamp(i0 + ivec2(1, 1), ivec2(0), last), 0);
	float lo = min(min(c00.a, c10.a), min(c01.a, c11.a));
	float hi = max(max(c00.a, c10.a), max(c01.a, c11.a));
	if (hi - lo > 0.5 / 255.0) {
		return c_near.rgb;
	}
	return mix(mix(c00.rgb, c10.rgb, f.x), mix(c01.rgb, c11.rgb, f.x), f.y);
}

void fragment() {
	vec3 base = field_rgb(UV);
	vec4 ov = texture(overlay, UV);
	int river_class = int(round(ov.r * 255.0));
	int lake = int(round(ov.g * 255.0));
	if (show_lakes && lake > 0) {
		base = lake_color.rgb;
	}
	if (show_rivers && river_class > 0 && river_class >= river_min_class) {
		base = river_colors[min(river_class, 7)].rgb;
	}

	vec3 col = base;
	if (shade_relief) {
		vec3 l = normalize(light_dir_view);
		vec3 n_sphere = normalize(v_sphere_normal);
		// 면 법선 (카메라 좌표). 구 바깥쪽을 보게 부호를 맞춥니다.
		vec3 n_face = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
		if (dot(n_face, n_sphere) < 0.0) {
			n_face = -n_face;
		}
		float lit = 1.0 + relief_shading * (dot(n_face, l) - dot(n_sphere, l));
		lit = clamp(lit, 1.0 - relief_shading_limit, 1.0 + relief_shading_limit);
		col = base * lit;
	}

	if (show_grid) {
		vec3 d = normalize(v_dir);
		float lat = degrees(asin(clamp(d.y, -1.0, 1.0)));
		float lon = degrees(atan(-d.z, d.x));
		// 경도 ±180° 에서 값이 뛰므로 180° 돌린 경도의 미분도 보고 작은 쪽을 씁니다.
		float lon_flip = degrees(atan(d.z, -d.x));
		float w_lat = max(fwidth(lat), 1e-4);
		float w_lon = max(min(fwidth(lon), fwidth(lon_flip)), 1e-4);
		// 극 가까이에서는 경선이 모여 뭉치므로 흐리게 합니다.
		float pole_fade = 1.0 - smoothstep(75.0, 88.0, abs(lat));
		float line_lat = grid_line(lat, w_lat, grid_step_deg);
		float line_lon = grid_line(lon, w_lon, grid_step_deg) * pole_fade;
		// 적도와 본초 자오선(0°)은 두 배 굵고 진하게 그립니다.
		float equator = grid_line(lat, 2.0 * w_lat, 360.0);
		float prime = grid_line(lon, 2.0 * w_lon, 360.0) * pole_fade;
		float line = max(line_lat, line_lon) * grid_opacity;
		line = max(line, max(equator, prime) * min(1.0, 2.0 * grid_opacity));
		col = mix(col, grid_color.rgb, line);
	}
	ALBEDO = col;
}
