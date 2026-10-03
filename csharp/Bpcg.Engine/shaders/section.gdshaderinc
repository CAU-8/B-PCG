// 자르는 면(단면)과 사각형 판정. 지형·동굴·물 셰이더가 같이 씁니다.
//
// 자르는 면: dot(world_pos, plane_normal) = plane_offset_m (전역 좌표, m).
// section_rect (x_min, z_min, x_max, z_max) 안에서만, 면의 법선 쪽(dot > plane_offset_m)을 버립니다.
// 버리지 않으려면 plane_offset_m 을 아주 크게 둡니다.

uniform vec3 plane_normal = vec3(1.0, 0.0, 0.0);
uniform float plane_offset_m = 100000.0;
uniform vec4 section_rect = vec4(-1.0e9, -1.0e9, 1.0e9, 1.0e9);

bool in_rect(vec4 r, vec2 xz) {
	return xz.x >= r.x && xz.y >= r.y && xz.x <= r.z && xz.y <= r.w;
}

bool section_cut(vec3 p) {
	return in_rect(section_rect, p.xz) && dot(p, normalize(plane_normal)) > plane_offset_m;
}
