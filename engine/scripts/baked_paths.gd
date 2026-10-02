class_name BakedPaths
extends RefCounted
## 구운 파일 폴더를 정합니다.
##
## 기본은 res://baked 입니다. 명령줄 끝에 `-- --baked-dir=<폴더>` 를 주면 그 폴더를 씁니다
## (검사가 임시 폴더의 작은 굽기 결과를 읽을 때 씁니다). 절대 경로도 됩니다.

const DEFAULT_DIR := "res://baked"
const ARG_PREFIX := "--baked-dir="


## 구운 파일 폴더 (끝에 / 없음).
static func dir() -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(ARG_PREFIX):
			return arg.trim_prefix(ARG_PREFIX).trim_suffix("/")
	return DEFAULT_DIR


## res://baked/ 로 시작하는 경로를 dir() 아래 경로로 바꿉니다. 다른 경로는 그대로 둡니다.
static func resolve(path: String) -> String:
	var prefix := DEFAULT_DIR + "/"
	if path.begins_with(prefix):
		return dir().path_join(path.trim_prefix(prefix))
	return path
