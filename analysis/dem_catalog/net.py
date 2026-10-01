"""dem_catalog 스크립트가 함께 쓰는 HTTPS 도우미 (표준 라이브러리만).

예전에는 macOS 에만 있는 인증서 파일 경로를 cafile 로 박아 두어서 Windows 에서는 돌지 않았습니다.
이제는 파이썬 기본 SSL 설정을 쓰고, 기본 설정에 인증서가 하나도 안 보이면 certifi(있을 때만)를 씁니다.
"""

import ssl
import urllib.request


def ssl_context() -> ssl.SSLContext:
    """운영체제 기본 인증서로 만든 SSL 설정. 비어 있으면 certifi 묶음으로 바꿉니다."""
    ctx = ssl.create_default_context()
    if ctx.cert_store_stats().get("x509_ca", 0) == 0:
        try:
            import certifi
        except ImportError:
            return ctx  # capath 방식(Linux)이면 비어 보여도 실제로는 동작함
        ctx = ssl.create_default_context(cafile=certifi.where())
    return ctx


CTX = ssl_context()


def get(url: str, timeout: float = 60, headers: dict | None = None):
    """GET 요청을 보내고 응답 객체를 돌려줍니다 (호출한 쪽에서 read)."""
    req = urllib.request.Request(url, headers=headers or {})
    return urllib.request.urlopen(req, timeout=timeout, context=CTX)
