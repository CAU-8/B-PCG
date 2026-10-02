// 지구본 둘레의 옅은 대기 빛 (보기 좋게 하려는 것뿐, 자료가 아닙니다).
//
// 지구본보다 조금 큰 구(반지름 glow_radius)에 씁니다. 시선이 지구본(해수면 구, 반지름
// globe_radius)을 지나는 자리에는 그리지 않으므로, 빛은 지구본 바깥의 테두리에만 보이고 지구본 위의
// 색(범례 색)을 바꾸지 않습니다. 테두리 안쪽(지구본 가장자리)이 가장 진하고 바깥으로 옅어집니다.
// G 키로 그늘과 함께 끕니다(globe.gd 가 이 구를 숨김).
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, shadows_disabled;

uniform vec4 glow_color : source_color = vec4(0.38, 0.62, 1.0, 1.0);
uniform float strength = 0.6;
// 클수록 빛이 지구본 가장자리에 얇게 모입니다.
uniform float falloff = 2.0;
// 지구본(해수면)과 이 구의 반지름 (모델 좌표 단위).
uniform float globe_radius = 1.0;
uniform float glow_radius = 1.06;

void fragment() {
	// 카메라 좌표: 카메라가 원점이고, 시선은 원점에서 이 조각(VERTEX) 쪽입니다.
	vec3 center = (VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0)).xyz;
	float scale = length(MODEL_MATRIX[0].xyz);
	vec3 ray = normalize(VERTEX);
	float along = dot(center, ray);
	// 지구본 중심에서 시선까지의 거리
	float miss = sqrt(max(dot(center, center) - along * along, 0.0));
	float r = globe_radius * scale;
	if (along > 0.0 && miss < r) {
		discard;
	}
	float t = clamp((miss - r) / max((glow_radius - globe_radius) * scale, 1e-4), 0.0, 1.0);
	ALBEDO = glow_color.rgb;
	ALPHA = strength * pow(1.0 - t, falloff);
}
