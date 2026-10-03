"""스튜디오의 필드 표(bpcg_studio.fields)가 C# 생성기의 표(src/Bpcg/Core/Fields.cs)와 같은지 봅니다.

C# 이 쓴 묶음 manifest 의 fields 항목(이름·단위·dtype·묶음·설명)과 비교합니다. 필드를 더하거나
바꾸면 두 표를 같이 고쳐야 이 시험이 통과합니다.
"""

from bpcg_studio.fields import FIELDS


def test_planet_manifest_fields_match_studio_table(planet):
    entries = {e["name"]: e for e in planet.manifest["fields"]}
    assert set(entries) == set(FIELDS)
    for name, info in FIELDS.items():
        e = entries[name]
        assert (e["unit"], e["dtype"], e["group"]) == (info.unit, info.dtype, info.group), name
        assert e["description"] == info.description, name


def test_hero_manifest_fields_are_known(hero, flat_hero):
    for b in (hero, flat_hero):
        for e in b.manifest["fields"]:
            info = FIELDS[e["name"]]
            assert (e["unit"], e["dtype"], e["group"]) == (info.unit, info.dtype, info.group)
