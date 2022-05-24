using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Base.Api.Extentions;
using Bikes.Api.Models;
using H3;
using H3.Algorithms;
using H3.Extensions;
using H3.Model;
using NetTopologySuite.Geometries;

namespace Bikes.Api.Utils
{
  public static class H3Helper
  {
    /// <summary>
    /// Размер ребра гексагона для каждого масштаба
    /// </summary>
    private static readonly (int EdgeLength, int Resolution)[] _EdgeLength =
    {
      (66, 10),
      (174, 9),
      (461, 8),
      (1221, 7),
      (3230, 6),
      (8544, 5),
      (22606, 4),
      (59811, 3),
      (158245, 2),
      (418676, 1),
      (110713, 0),
      (int.MaxValue, 0)
    };

    /// <summary>
    /// Отдает по координатам сокращенный индекс H3, в котором присутствуют только биты с номерами ячеек без префикса со служебной информацией 
    /// </summary>
    /// <param name="resolution">Масштаб индекса</param>
    /// <returns>64 битное представление сокращенного индекса</returns>
    public static ulong GetH3Compact(double lon, double lat, int resolution)
    {
      var h3Index = H3Index.FromGeoCoord(new GeoCoord(lat, lon), resolution);
      return GetH3Compact(h3Index);
    }

    /// <summary>
    /// Отдает по координатам сокращенный индекс H3, в котором присутствуют только биты с номерами ячеек без префикса со служебной информацией 
    /// </summary>
    /// <param name="resolution">Масштаб индекса</param>
    /// <returns>64 битное представление сокращенного индекса</returns>
    public static ulong GetH3Compact(Point point, int resolution)
    {
      var h3Index = H3Index.FromPoint(point, resolution);
      return GetH3Compact(h3Index);
    }

    /// <summary>
    /// Отдает по существующему H3 индексу сокращенный индекс H3, в котором присутствуют только биты с номерами ячеек без префикса со служебной информацией 
    /// </summary>
    /// <returns>64 битное представление сокращенного индекса</returns>
    public static ulong GetH3Compact(H3Index h3Index)
    {
      var longIndex = (ulong)h3Index;
      var cellsNumbersOnly =
        longIndex &
        0b1111111111111111111111111111111111111111111111111111; //Оставляет только 52 бит с номерами ячеек  
      return cellsNumbersOnly;
    }

    /// <summary>
    /// Отдает выражение для поиска по сокращенному индексу H3 ТС в заданном радиусе
    /// </summary>
    /// <returns>Выражение для поиска</returns>
    public static Expression<Func<Bike, bool>> FilterByRadius(Point point, double radiusByMeters)
    {
      var ranges = GetH3Ranges(point, radiusByMeters);
      return FilterByRanges(ranges);
    }

    /// <summary>
    /// Отдает выражение для поиска по сокращенному индексу H3 ТС в заданном радиусе
    /// </summary>
    /// <returns>Выражение для поиска</returns>
    public static Expression<Func<Bike, bool>> FilterByRadius(double latitude, double longitude, double radiusByMeters)
    {
      var ranges = GetH3Ranges(latitude, longitude, radiusByMeters);
      return FilterByRanges(ranges);
    }

    /// <summary>
    /// Возвращает интервал в сокращенном индексе для поиска по вхождению в заданный гексагон
    /// </summary>
    /// <param name="h3Index">Поисковый гексагон</param>
    /// <returns>Интервал возможных значений сокращенного индекса. Если сокращенный индекс ТС входит в интервал, значит ТС находится внутри гексагона</returns>
    public static Range GetH3QueryRange(H3Index h3Index)
    {
      var finestResolutionDiff = 15 - h3Index.Resolution;
      var bits = finestResolutionDiff * 3;
      var rangeSize = ((ulong)1 << bits) - 1;
      var upperBound = GetH3Compact(h3Index);
      var lowerBound = upperBound - rangeSize;
      return new Range(lowerBound, upperBound);
    }

    
    /// <summary>
    /// Объединяет пересекающиеся и соприкасающиеся интервалы
    /// </summary>
    public static IEnumerable<Range> UnionRanges(IEnumerable<Range> ranges)
    {
      var ordered = ranges.OrderBy(r => r.LowerBound).ToArray();
      for (var i = 0; i < ordered.Length; i++)
      {
        var checkingRange = ordered[i];
        for (var j = i + 1; j < ordered.Length; j++)
        {
          if (ordered[j].LowerBound > ordered[i].UpperBound + 1)
            break;
          checkingRange = checkingRange.UnionWith(ordered[j]);
          i++;
        }

        yield return checkingRange;
      }
    }
    
    private static Expression<Func<Bike, bool>> FilterByRanges(IEnumerable<Range> ranges)
    {
      Expression<Func<Bike, bool>> exp = bike => false;
      foreach (var range in ranges)
        exp = exp.OrElse(b => range.LowerBound <= b.H3Index && b.H3Index <= range.UpperBound);

      return exp;
    }

    private static IEnumerable<Range> GetH3Ranges(double latitude, double longitude, double radiusByMeters)
    {
      var finestResolution = GetResolution(radiusByMeters);

      var h3Index = H3Index.FromGeoCoord(new GeoCoord(latitude, longitude), finestResolution);
      return GetH3Ranges(h3Index, radiusByMeters);
    }
    
    private static IEnumerable<Range> GetH3Ranges(Point point, double radiusByMeters)
    {
      var finestResolution = GetResolution(radiusByMeters);
      var h3Index = H3Index.FromPoint(point, finestResolution);
      return GetH3Ranges(h3Index, radiusByMeters);
    }

    public static int GetResolution(double radiusByMeters)
    {
      //Выбираем такой масштаб, что радиус поиска был не менее 3-х радиусов (=ребро) соответствующего гексагона.
      return _EdgeLength.First(f => f.EdgeLength * 3 > radiusByMeters).Resolution;
    }

    public static (IEnumerable<ulong> Hexagons, int Resolution) GetH3RingByRadius(Point point, int radiusByMeters)
    { 
      var resolution = H3Helper.GetResolution(radiusByMeters);
      var h3Index = H3Index.FromPoint(point, resolution);
      var ring = GetH3RingByRadius(h3Index, radiusByMeters);
      return (ring.Select(h => (ulong)h), resolution);
    }
    
    private static IEnumerable<Range> GetH3Ranges(H3Index h3Index, double radiusByMeters)
    {
      //Количество
      var ring = GetH3RingByRadius(h3Index, radiusByMeters);
      var ranges = ring
        //.Compact()
        .Select(GetH3QueryRange);
      ranges = UnionRanges(ranges);
      return ranges;
    }

    private static IEnumerable<H3Index> GetH3RingByRadius(H3Index h3Index, double radiusByMeters)
    {
      var hexRadius = (int)Math.Truncate(radiusByMeters / (h3Index.GetRadiusInKm() * 2.5 * 1000)) + 1;
      var ring = h3Index.GetKRing(hexRadius).Select(r => r.Index);
      return ring;
    }


    public class Range
    {
      public ulong LowerBound { get; }
      public ulong UpperBound { get; }

      public Range(ulong lowerBound, ulong upperBound)
      {
        LowerBound = lowerBound;
        UpperBound = upperBound;
      }

      public Range UnionWith(Range other)
      {
        return new Range(LowerBound, Math.Max(UpperBound, other.UpperBound));
      }
    }
  }
}
