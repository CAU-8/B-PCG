"""히어로 유역(L2, 평면 25 m): 후보 찾기 → L0 경계조건 → 같은 2~4단계 (pipeline.md 9장).

중간 스케일. L0 없이 가짜 경계조건으로 도는 평면 히어로(flat_hero)도 있습니다('평면 히어로 먼저').

- finder.find_hero(planet, cfg) -> HeroSite
- domain.hero_graph(site, cfg) -> (graph, unit_points)
- refine.refine_hero(planet, site, cfg) -> pipeline.HeroState
- flat.flat_hero(cfg) -> pipeline.HeroState
"""

from bpcg.hero.domain import hero_graph
from bpcg.hero.finder import HeroSite, find_hero
from bpcg.hero.flat import flat_hero
from bpcg.hero.refine import refine_hero

__all__ = ["HeroSite", "find_hero", "flat_hero", "hero_graph", "refine_hero"]
