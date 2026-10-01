// 재질 부피(strata.u8) 읽기. StrataVolume.apply_to() 가 uniform 을 채웁니다.
//
// 3D 텍스처 축: 가로(u) = 열(+X), 세로(v) = 층(위에서 아래로), 깊이(w) = 행(+Z).
// 열 c, 행 r 기둥의 가운데는 X = origin.x + c·dx, Z = origin.z + r·dz 입니다.
// 층 k 의 가운데는 Y = (그 기둥의 윗면) − (k + 0.5)·dy 입니다 (지표를 따라가는 층).
// 재질 번호: 0..11 암석(geology.rocks), 254 물, 255 공기(동굴).

uniform bool has_strata = false;
uniform sampler3D strata : filter_nearest, repeat_disable;
// 윗면 높이 (strata_top, 값 + strata_origin.y = 엔진 Y)
uniform sampler2D strata_top : filter_linear, repeat_disable;
// 재질 번호 → 색 (256 × 1)
uniform sampler2D palette : source_color, filter_nearest, repeat_disable;
// (0번 열 X, 윗면 값에 더할 Y, 0번 행 Z)
uniform vec3 strata_origin = vec3(0.0);
// (dx, dy, dz) m
uniform vec3 strata_spacing = vec3(4.0, 2.0, 4.0);
// (열 수, 층 수, 행 수)
uniform vec3 strata_size = vec3(1.0);

bool strata_covers(vec2 xz) {
	vec2 g = (xz - strata_origin.xz) / strata_spacing.xz + 0.5;
	return has_strata && g.x >= 0.0 && g.y >= 0.0 && g.x <= strata_size.x && g.y <= strata_size.z;
}

// 재질 부피 윗면의 엔진 Y (쌍선형).
float strata_top_y(vec2 xz) {
	vec2 uv = ((xz - strata_origin.xz) / strata_spacing.xz + 0.5) / strata_size.xz;
	return texture(strata_top, uv).r + strata_origin.y;
}

// (x, z) 기둥의 layer 번째 층(실수, 0 = 맨 위)의 재질 번호.
float strata_id(vec2 xz, float layer) {
	vec3 uvw = vec3(
			((xz.x - strata_origin.x) / strata_spacing.x + 0.5) / strata_size.x,
			clamp(layer, 0.0, strata_size.y - 0.5) / strata_size.y,
			((xz.y - strata_origin.z) / strata_spacing.z + 0.5) / strata_size.z);
	return floor(texture(strata, uvw).r * 255.0 + 0.5);
}

vec3 strata_color(float id) {
	return texture(palette, vec2((id + 0.5) / 256.0, 0.5)).rgb;
}
