using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hero;
using Xunit;

namespace Bpcg.Tests.Hero;

/// <summary>설정 hero.site_lat_lon_deg 로 히어로 자리를 고르는 길 (Finder.FindHero, Finder.UnitFromLatLon).</summary>
public sealed class HeroSiteTests
{
    private static readonly Lazy<PlanetState> Planet = new(() => Pipeline.GeneratePlanet(Tiny(), null));

    private static Config Tiny(params double[] site)
    {
        var list = new List<object?>();
        foreach (double v in site)
        {
            list.Add(v);
        }
        return Config.LoadConfig("earth", "tiny")
            .WithOverrides(new List<KeyValuePair<string, object?>> { new("hero.site_lat_lon_deg", list) });
    }

    [Fact]
    public void UnitFromLatLonInvertsSiteFromCell()
    {
        Config cfg = Tiny();
        foreach (long cell in new long[] { 0, 591, 2721, Planet.Value.Graph.NCells - 1 })
        {
            HeroSite site = Finder.SiteFromCell(Planet.Value, cell, cfg);
            double[] u = Finder.UnitFromLatLon(site.LatDeg, site.LonDeg, cfg);
            for (int k = 0; k < 3; k++)
            {
                Assert.Equal(site.CenterUnit[k], u[k], 12);
            }
        }
    }

    [Fact]
    public void EmptySiteKeepsScoreChoiceAndSameSiteGivesSameCell()
    {
        Assert.Null(Finder.RequestedSite(Tiny()));
        HeroSite auto = Finder.FindHero(Planet.Value, Tiny());
        HeroSite picked = Finder.FindHero(Planet.Value, Tiny(auto.LatDeg, auto.LonDeg));
        Assert.Equal(auto.L0Cell, picked.L0Cell);
        Assert.Equal(auto.Score, picked.Score);
    }

    [Fact]
    public void OceanSiteStopsWithNearestCandidateThatWorks()
    {
        RunError e = Assert.Throws<RunError>(() => Finder.FindHero(Planet.Value, Tiny(0.0, 0.0)));
        Assert.Contains("바다 위", e.Message);
        int open = e.Message.LastIndexOf('[');
        int close = e.Message.LastIndexOf(']');
        string[] parts = e.Message[(open + 1)..close].Split(", ");
        double lat = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        double lon = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        HeroSite site = Finder.FindHero(Planet.Value, Tiny(lat, lon));
        Assert.Equal(lat, site.LatDeg, 2);
        Assert.Equal(lon, site.LonDeg, 2);
    }

    [Fact]
    public void BadSiteValuesStopWithRunError()
    {
        Assert.Throws<RunError>(() => Finder.RequestedSite(Tiny(1.0, 2.0, 3.0)));
        Assert.Throws<RunError>(() => Finder.RequestedSite(Tiny(91.0, 0.0)));
        Assert.Throws<RunError>(() => Finder.RequestedSite(Tiny(0.0, 181.0)));
    }
}
