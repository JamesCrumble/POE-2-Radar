﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2.Shared.Helpers;
using GameOffsets2;
using GameOffsets2.Native;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace Radar;

public partial class Radar
{
    private Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task> _addRouteAction;
    private Func<Color> _getColor;

    private void LoadTargets()
    {
        var fileText = File.ReadAllText(Path.Combine(DirectoryFullName, "targets.json"));
        _targetDescriptions = JsonConvert.DeserializeObject<ConcurrentDictionary<string, List<TargetDescription>>>(fileText);
    }

    private void RestartPathFinding()
    {
        StopPathFinding();
        StartPathFinding();
    }

    private void StartPathFinding()
    {
        if (Settings.PathfindingSettings.ShowPathsToTargetsOnMap)
        {
            var e = Enumerable.Repeat(RainbowColors, 100).SelectMany(x => x).GetEnumerator();
            _getColor = () =>
            {
                e.MoveNext();
                return e.Current;
            };
            var pf = new PathFinder(_processedTerrainData, new[] { 1, 2, 3, 4, 5 });
            _addRouteAction = (point, callback, cancellationToken) => Task.Run(() => FindPath(pf, point, callback, cancellationToken), cancellationToken);
            foreach (var location in _clusteredTargetLocations.SelectMany(x => x.Value.Locations).Distinct())
            {
                AddRoute(location);
            }

        }
    }

    private void AddRoute(Vector2 target)
    {
        var color = _getColor();

        Color GetWorldColor() => Settings.PathfindingSettings.WorldPathSettings.UseRainbowColorsForPaths
            ? color
            : Color.FromRgba(Settings.PathfindingSettings.WorldPathSettings.DefaultPathColor.Value.ToRgba());

        Color GetMapColor() => Settings.PathfindingSettings.UseRainbowColorsForMapPaths
            ? color
            : Color.FromRgba(Settings.PathfindingSettings.DefaultMapPathColor.Value.ToRgba());

        var routes = _routes;
        AddRoute(target, path =>
        {
            if (path != null)
            {
                var rd = new RouteDescription { Path = path, MapColor = GetMapColor, WorldColor = GetWorldColor };
                routes.AddOrUpdate(target, rd, (_, _) => rd);
            }
        }, _findPathsCts.Token);
    }

    private Task AddRoute(Vector2 target, Action<List<Vector2i>> callback, CancellationToken cancellationToken)
    {
        if (_addRouteAction == null)
        {
            return Task.FromException(new Exception("Pathfinding wasn't started yet"));
        }

        return _addRouteAction(target, callback, cancellationToken);
    }

    private void StopPathFinding()
    {
        _findPathsCts.Cancel();
        _findPathsCts = new CancellationTokenSource();
        _routes = new ConcurrentDictionary<Vector2, RouteDescription>();
        _addRouteAction = null;
    }

    private async Task WaitUntilPluginEnabled(CancellationToken cancellationToken)
    {
        while (!Settings.Enable)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task FindPath(PathFinder pf, Vector2 point, Action<List<Vector2i>> callback, CancellationToken cancellationToken)
    {
        var playerPosition = GetPlayerPosition();
        foreach (var elem in pf.RunFirstScan(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y)))
        {
            await WaitUntilPluginEnabled(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (elem.Any())
            {
                callback(elem);
            }
        }

        while (true)
        {
            await WaitUntilPluginEnabled(cancellationToken);
            var newPosition = GetPlayerPosition();
            if (playerPosition == newPosition)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            playerPosition = newPosition;
            var path = pf.FindPath(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y));
            callback(path);
        }
    }

    private ConcurrentDictionary<string, List<Vector2i>> GetTargets()
    {
        var ret = new ConcurrentDictionary<string, List<Vector2i>>(GetTileTargets()
            .ToLookup(x => x.Key, x => x.Value)
            .ToDictionary(x => x.Key, x => x.SelectMany(v => v).ToList()));

        return ret;
    }

    private Dictionary<string, List<Vector2i>> GetTileTargets()
    {
        var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TgtArray);
        var ret = new ConcurrentDictionary<string, ConcurrentQueue<Vector2i>>();
        Parallel.For(0, tileData.Length, tileNumber =>
        {
            var tgtTileStruct = GameController.Memory.Read<TgtTileStruct>(tileData[tileNumber].TgtFilePtr);
            var key2 = GameController.Memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr)
                .name.ToString(GameController.Memory);
            var coordinate = new Vector2i(
                tileNumber % _terrainMetadata.NumCols * TileToGridConversion,
                tileNumber / _terrainMetadata.NumCols * TileToGridConversion);

            if (Settings.PathfindingSettings.IncludeTilePathsAsTargets)
            {
                var key1 = tgtTileStruct.TgtPath.ToString(GameController.Memory);
                if (!string.IsNullOrEmpty(key1))
                {
                    ret.GetOrAdd(key1, _ => new ConcurrentQueue<Vector2i>()).Enqueue(coordinate);
                }
            }

            if (!string.IsNullOrEmpty(key2))
            {
                ret.GetOrAdd(key2, _ => new ConcurrentQueue<Vector2i>()).Enqueue(coordinate);
            }
        });


        return ret.ToDictionary(k => k.Key, k => k.Value.ToList());
    }

    private bool IsDescriptionInArea(string descriptionAreaPattern)
    {
        return GameController.Area.CurrentArea.Area.RawName.Like(descriptionAreaPattern);
    }

    private IEnumerable<TargetDescription> GetTargetDescriptionsInArea()
    {
        return _targetDescriptions.Where(x => IsDescriptionInArea(x.Key)).SelectMany(x => x.Value);
    }

    private ConcurrentDictionary<string, TargetLocations> ClusterTargets()
    {
        var tileMap = new ConcurrentDictionary<string, TargetLocations>();
        Parallel.ForEach(_targetDescriptionsInArea.Values, new ParallelOptions { MaxDegreeOfParallelism = 1 }, target =>
        {
            var locations = ClusterTarget(target);
            if (locations != null)
            {
                tileMap[target.Name] = locations;
            }
        });
        return tileMap;
    }

    private IReadOnlyCollection<Vector2i> GetLocationsFromTilePattern(string tilePattern)
    {
        var regex = tilePattern.ToLikeRegex();
        return _allTargetLocations.Where(x => regex.IsMatch(x.Key)).SelectMany(x => x.Value).ToList();
    }

    private TargetLocations ClusterTarget(TargetDescription target)
    {
        var expectedCount = target.ExpectedCount;
        var targetName = target.Name;
        var locations = ClusterTarget(targetName, expectedCount);
        if (locations == null) return null;
        return new TargetLocations
        {
            Locations = locations,
            DisplayName = target.DisplayName
        };
    }

    private Vector2[] ClusterTarget(string targetName, int expectedCount)
    {
        var tileList = GetLocationsFromTilePattern(targetName);
        if (tileList is not { Count: > 0 })
        {
            return null;
        }

        var clusterIndexes = KMeans.Cluster(tileList.Select(x => new Vector2d(x.X, x.Y)).ToArray(), expectedCount);
        var resultList = new List<Vector2>();
        foreach (var tileGroup in tileList.Zip(clusterIndexes).GroupBy(x => x.Second))
        {
            var v = new Vector2();
            var count = 0;
            foreach (var (vector, _) in tileGroup)
            {
                var mult = IsGridWalkable(vector) ? 100 : 1;
                v += mult * vector.ToVector2();
                count += mult;
            }

            v /= count;
            var replacement = tileGroup.Select(tile => new Vector2i(tile.First.X, tile.First.Y))
                .Where(IsGridWalkable)
                .OrderBy(x => (x.ToVector2() - v).LengthSquared())
                .Select(x => (Vector2i?)x)
                .FirstOrDefault();
            if (replacement != null)
            {
                v = replacement.Value.ToVector2();
            }

            if (!IsGridWalkable(v.Truncate()))
            {
                v = GetAllNeighborTiles(v.Truncate()).First(IsGridWalkable).ToVector2();
            }

            resultList.Add(v);
        }

        var locations = resultList.Distinct().ToArray();
        return locations;
    }

    private bool IsGridWalkable(Vector2i tile)
    {
        return _processedTerrainData[tile.Y][tile.X] is 5 or 4;
    }

    private IEnumerable<Vector2i> GetAllNeighborTiles(Vector2i start)
    {
        foreach (var range in Enumerable.Range(1, 100000))
        {
            var xStart = Math.Max(0, start.X - range);
            var yStart = Math.Max(0, start.Y - range);
            var xEnd = Math.Min(_areaDimensions.Value.X, start.X + range);
            var yEnd = Math.Min(_areaDimensions.Value.Y, start.Y + range);
            for (var x = xStart; x <= xEnd; x++)
            {
                yield return new Vector2i(x, yStart);
                yield return new Vector2i(x, yEnd);
            }

            for (var y = yStart + 1; y <= yEnd - 1; y++)
            {
                yield return new Vector2i(xStart, y);
                yield return new Vector2i(xEnd, y);
            }

            if (xStart == 0 && yStart == 0 && xEnd == _areaDimensions.Value.X && yEnd == _areaDimensions.Value.Y)
            {
                break;
            }
        }
    }
}