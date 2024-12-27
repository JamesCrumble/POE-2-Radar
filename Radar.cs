﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using ExileCore2;
using System.Threading.Tasks;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ExileCore2.Shared.Helpers;
using GameOffsets2;
using GameOffsets2.Native;
using ImGuiNET;
using Color = SharpDX.Color;
using Positioned = ExileCore2.PoEMemory.Components.Positioned;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileCore2.PoEMemory.FilesInMemory;
using static System.Net.Mime.MediaTypeNames;
using ExileCore2.PoEMemory.Components;

namespace Radar;

public partial class Radar : BaseSettingsPlugin<RadarSettings>
{
    private const string TextureName = "radar_minimap";
    private const int TileToGridConversion = 23;
    private const int TileToWorldConversion = 250;
    public const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);
    private double _mapScale;

    private ConcurrentDictionary<string, List<TargetDescription>> _targetDescriptions = new();
    private Vector2i? _areaDimensions;
    private TerrainData _terrainMetadata;
    private float[][] _heightData;
    private int[][] _processedTerrainData;
    private Dictionary<string, TargetDescription> _targetDescriptionsInArea = new();
    private HashSet<string> _currentZoneTargetEntityPaths = new();
    private CancellationTokenSource _findPathsCts = new CancellationTokenSource();
    private ConcurrentDictionary<string, TargetLocations> _clusteredTargetLocations = new();
    private ConcurrentDictionary<string, List<Vector2i>> _allTargetLocations = new();
    private ConcurrentDictionary<Vector2i, List<string>> _locationsByPosition = new();
    private RectangleF _rect;
    private ImDrawListPtr _backGroundWindowPtr;
    private ConcurrentDictionary<Vector2, RouteDescription> _routes = new();

    public override bool Initialise()
    {
        GameController.PluginBridge.SaveMethod("Radar.LookForRoute",
            (Vector2 target, Action<List<Vector2i>> callback, CancellationToken cancellationToken) => AddRoute(target, callback, cancellationToken));
        GameController.PluginBridge.SaveMethod("Radar.ClusterTarget",
            (string targetName, int expectedCount) => ClusterTarget(targetName, expectedCount));

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        StopPathFinding();
        if (GameController.Game.IsInGameState || GameController.Game.IsEscapeState)
        {
            _targetDescriptionsInArea = GetTargetDescriptionsInArea().ToDictionary(x => x.Name);
            _currentZoneTargetEntityPaths = _targetDescriptionsInArea.Values.Where(x => x.TargetType == TargetType.Entity).Select(x => x.Name).ToHashSet();
            _terrainMetadata = GameController.IngameState.Data.DataStruct.Terrain;
            _heightData = GameController.IngameState.Data.RawTerrainHeightData;
            _allTargetLocations = GetTargets();
            _locationsByPosition = new ConcurrentDictionary<Vector2i, List<string>>(_allTargetLocations
                .SelectMany(x => x.Value.Select(y => (x.Key, y)))
                .ToLookup(x => x.y, x => x.Key)
                .ToDictionary(x => x.Key, x => x.ToList()));
            _areaDimensions = GameController.IngameState.Data.AreaDimensions;
            _processedTerrainData = GameController.IngameState.Data.RawPathfindingData;
            GenerateMapTexture();
            _clusteredTargetLocations = ClusterTargets();
            StartPathFinding();
        }
    }

    public override void DrawSettings()
    {
        Settings.PathfindingSettings.CurrentZoneName.Value = GameController.Area.CurrentArea.Area.RawName;
        base.DrawSettings();
    }

    private static readonly List<Color> RainbowColors = new List<Color>
    {
        Color.Red,
        Color.Green,
        Color.Blue,
        Color.Yellow,
        Color.Violet,
        Color.Orange,
        Color.White,
        Color.LightBlue,
        Color.Indigo,
    };

    public override void OnLoad()
    {
        LoadTargets();
        Settings.Reload.OnPressed = () =>
        {
            LoadTargets();
            AreaChange(GameController.Area.CurrentArea);
        };
        Settings.MaximumPathCount.OnValueChanged += (_, _) => { RestartPathFinding(); };
        Settings.TerrainColor.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.DrawHeightMap.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.SkipEdgeDetector.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.SkipNeighborFill.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.SkipRecoloring.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.DisableHeightAdjust.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.MaximumMapTextureDimension.OnValueChanged += (_, _) => { GenerateMapTexture(); };
    }

    public override void EntityAdded(Entity entity)
    {
        var positioned = entity.GetComponent<Positioned>();
        if (positioned != null)
        {
            var path = entity.Path;
            if (_currentZoneTargetEntityPaths.Contains(path))
            {
                bool alreadyContains = false;
                var truncatedPos = positioned.GridPos.Truncate();
                _allTargetLocations.AddOrUpdate(path, _ => new List<Vector2i> { truncatedPos },
                    // ReSharper disable once AssignmentInConditionalExpression
                    (_, l) => (alreadyContains = l.Contains(truncatedPos)) ? l : l.Append(truncatedPos).ToList());
                _locationsByPosition.AddOrUpdate(truncatedPos, _ => new List<string> { path },
                    (_, l) => l.Contains(path) ? l : l.Append(path).ToList());
                if (!alreadyContains)
                {
                    var targetDesc = _targetDescriptionsInArea[path];
                    var oldValue = _clusteredTargetLocations.GetValueOrDefault(path);
                    var newValue = _clusteredTargetLocations.AddOrUpdate(path,
                        _ => ClusterTarget(targetDesc),
                        (_, _) => ClusterTarget(targetDesc)
                    );
                    foreach (var newLocation in newValue.Locations.Except(oldValue.Locations ?? Array.Empty<Vector2>()))
                    {
                        AddRoute(newLocation);
                    }
                }
            }
        }
    }

    private Vector2 GetPlayerPosition()
    {
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerPositionComponent = player.GetComponent<Positioned>();
        if (playerPositionComponent == null)
            return new Vector2(0, 0);
        var playerPosition = new Vector2(playerPositionComponent.GridX, playerPositionComponent.GridY);
        return playerPosition;
    }

    public override void Render()
    {
        var ingameUi = GameController.Game.IngameState.IngameUi;
        if (!Settings.Debug.IgnoreFullscreenPanels &&
            ingameUi.FullscreenPanels.Any(x => x.IsVisible))
        {
            return;
        }

        if (!Settings.Debug.IgnoreLargePanels &&
            ingameUi.LargePanels.Any(x => x.IsVisible))
        {
            return;
        }

        _rect = GameController.Window.GetWindowRectangle() with { Location = Vector2.Zero };
        if (!Settings.Debug.DisableDrawRegionLimiting)
        {
            if (ingameUi.OpenRightPanel.IsVisible)
            {
                _rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
            }

            if (ingameUi.OpenLeftPanel.IsVisible)
            {
                _rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;
            }
        }

        ImGui.SetNextWindowSize(new Vector2(_rect.Width, _rect.Height));
        ImGui.SetNextWindowPos(new Vector2(_rect.Left, _rect.Top));

        ImGui.Begin("radar_background",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        _backGroundWindowPtr = ImGui.GetWindowDrawList();
        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();

        var mapCenter = largeMap.GetClientRect().TopLeft + largeMap.Shift + largeMap.DefaultShift +
                            new Vector2(Settings.Debug.MapCenterOffsetX, Settings.Debug.MapCenterOffsetY);
        //I have ABSOLUTELY NO IDEA where 677 comes from, but it works perfectly in all configurations I was able to test. Aspect ratio doesn't matter, just camera height
        _mapScale = GameController.IngameState.Camera.Height / 677f * largeMap.Zoom * Settings.CustomScale;
        DrawLargeMap(mapCenter);
        DrawTargets(mapCenter);

        // if (largeMap.IsVisible)
        // {
        //     var mapCenter = largeMap.GetClientRect().TopLeft.ToVector2Num() + largeMap.ShiftNum + largeMap.DefaultShiftNum +
        //                     new Vector2(Settings.Debug.MapCenterOffsetX, Settings.Debug.MapCenterOffsetY);
        //     //I have ABSOLUTELY NO IDEA where 677 comes from, but it works perfectly in all configurations I was able to test. Aspect ratio doesn't matter, just camera height
        //     _mapScale = GameController.IngameState.Camera.Height / 677f * largeMap.Zoom * Settings.CustomScale;
        //     DrawLargeMap(mapCenter);
        //     DrawTargets(mapCenter);
        // }

        DrawWorldPaths(largeMap);
        ImGui.End();
    }

    private void DrawWorldPaths(SubMap largeMap)
    {
        if (Settings.PathfindingSettings.WorldPathSettings.ShowPathsToTargets &&
            (!largeMap.IsVisible || !Settings.PathfindingSettings.WorldPathSettings.ShowPathsToTargetsOnlyWithClosedMap))
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerRender = player?.GetComponent<ExileCore2.PoEMemory.Components.Render>();
            if (playerRender == null)
                return;
            var initPos = GameController.IngameState.Camera.WorldToScreen(playerRender.Pos with { Z = playerRender.RenderStruct.Height });
            foreach (var (route, offsetAmount) in _routes.Values
                         .GroupBy(x => x.Path.Count < 2 ? 0 : (x.Path[1] - x.Path[0]) switch { var diff => Math.Atan2(diff.Y, diff.X) })
                         .SelectMany(group => group.Select((route, i) => (route, i - group.Count() / 2.0f + 0.5f))))
            {
                var p0 = initPos;
                var p0WithOffset = p0;
                var i = 0;
                foreach (var elem in route.Path)
                {
                    var p1 = GameController.IngameState.Camera.WorldToScreen(
                        new Vector3(elem.X * GridToWorldMultiplier, elem.Y * GridToWorldMultiplier, _heightData[elem.Y][elem.X]));
                    var offsetDirection = Settings.PathfindingSettings.WorldPathSettings.OffsetPaths
                        ? (p1 - p0) switch { var s => new Vector2(s.Y, -s.X) / s.Length() }
                        : Vector2.Zero;
                    var finalOffset = offsetDirection * offsetAmount * Settings.PathfindingSettings.WorldPathSettings.PathThickness;
                    p0 = p1;
                    p1 += finalOffset;
                    if (++i % Settings.PathfindingSettings.WorldPathSettings.DrawEveryNthSegment == 0)
                    {
                        if (_rect.Contains(p0WithOffset) || _rect.Contains(p1))
                        {

                            Graphics.DrawLine(p0WithOffset, p1, Settings.PathfindingSettings.WorldPathSettings.PathThickness, System.Drawing.Color.FromArgb(route.WorldColor().ToRgba()));
                        }
                        else
                        {
                            break;
                        }
                    }

                    p0WithOffset = p1;
                }
            }
        }
    }

    private void DrawBox(Vector2 p0, Vector2 p1, Color color)
    {
        _backGroundWindowPtr.AddRectFilled(p0, p1, (uint)color.ToRgba());
    }

    private void DrawText(string text, Vector2 pos, Color color)
    {
        _backGroundWindowPtr.AddText(pos, (uint)color.ToRgba(), text);
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier; //z is normally "world" units, translate to grid
        return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos, (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private void DrawLargeMap(Vector2 mapCenter)
    {
        if (!Settings.DrawWalkableMap || !Graphics.HasImage(TextureName) || _areaDimensions == null)
            return;
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player.GetComponent<ExileCore2.PoEMemory.Components.Render>();

        if (playerRender == null)
            return;

        //DebugWindow.LogMsg($"playerRender.GridPos(): {playerRender.GridPos()}");
        //DebugWindow.LogMsg($"playerRender.RenderStruct.Height: {playerRender.RenderStruct.Height}");
        var rectangleF = new RectangleF(-playerRender.GridPos().X, -playerRender.GridPos().Y, _areaDimensions.Value.X, _areaDimensions.Value.Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var p1 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Top), playerHeight);
        var p2 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Top), playerHeight);
        var p3 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Bottom), playerHeight);
        var p4 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Bottom), playerHeight);

        //DebugWindow.LogMsg($"(p1: {p1}, p2: {p2}, p3: {p3}, p4: {p4})");
        Graphics.DrawQuad(Graphics.GetTextureId(TextureName), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        _backGroundWindowPtr.AddImageQuad(Graphics.GetTextureId(TextureName), p1, p2, p3, p4);
    }

    private void DrawTargets(Vector2 mapCenter)
    {
        var color = Settings.PathfindingSettings.TargetNameColor.Value;
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player.GetComponent<ExileCore2.PoEMemory.Components.Render>();
        if (playerRender == null)
            return;
        var playerPosition = new Vector2(playerRender.GridPos().X, playerRender.GridPos().Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var ithElement = 0;
        if (Settings.PathfindingSettings.ShowPathsToTargetsOnMap)
        {
            foreach (var route in _routes.Values)
            {
                ithElement++;
                ithElement %= 5;
                foreach (var elem in route.Path.Skip(ithElement).GetEveryNth(5))
                {
                    var mapDelta = TranslateGridDeltaToMapDelta(new Vector2(elem.X, elem.Y) - playerPosition, playerHeight + _heightData[elem.Y][elem.X]);
                    DrawBox(mapCenter + mapDelta - new Vector2(2, 2), mapCenter + mapDelta + new Vector2(2, 2), route.MapColor());
                }
            }
        }

        if (Settings.ShowRareMonsters)
        {
            Vector2 position, mapDelta, mapPos;
            foreach (var entity in GameController.Entities)
            {
                if (entity.Type != ExileCore2.Shared.Enums.EntityType.Monster) continue;
                if (entity.Rarity != ExileCore2.Shared.Enums.MonsterRarity.Rare && entity.Rarity != ExileCore2.Shared.Enums.MonsterRarity.Unique) continue;

                position = entity.GridPos;
                mapDelta = TranslateGridDeltaToMapDelta(position - playerPosition, playerHeight + _heightData[(int)position.Y][(int)position.X]);
                mapPos = mapCenter + mapDelta;
                _backGroundWindowPtr.AddCircleFilled(mapPos, 4, (uint)Color.Gold.ToRgba());
            }
        }

        if (Settings.PathfindingSettings.ShowAllTargets)
        {
            foreach (var (location, texts) in _locationsByPosition)
            {
                var regex = string.IsNullOrEmpty(Settings.PathfindingSettings.TargetNameFilter)
                    ? null
                    : new Regex(Settings.PathfindingSettings.TargetNameFilter);

                bool TargetFilter(string t) =>
                    (regex?.IsMatch(t) ?? true) &&
                    _allTargetLocations.GetValueOrDefault(t) is { } list && list.Count <= Settings.PathfindingSettings.MaxTargetNameCount;

                var text = string.Join("\n", texts.Distinct().Where(TargetFilter));
                var textOffset = Graphics.MeasureText(text) / 2f;
                var mapDelta = TranslateGridDeltaToMapDelta(location - playerPosition, playerHeight + _heightData[location.Y][location.X]);
                var mapPos = mapCenter + mapDelta;
                if (Settings.PathfindingSettings.EnableTargetNameBackground)
                    DrawBox(mapPos - textOffset, mapPos + textOffset, Color.Black);
                DrawText(text, mapPos - textOffset, Color.FromRgba(color.ToRgba()));
            }
        }
        else if (Settings.PathfindingSettings.ShowSelectedTargets)
        {
            foreach (var (name, description) in _clusteredTargetLocations)
            {
                foreach (var clusterPosition in description.Locations)
                {
                    float clusterHeight = 0;
                    if (clusterPosition.X < _heightData[0].Length && clusterPosition.Y < _heightData.Length)
                        clusterHeight = _heightData[(int)clusterPosition.Y][(int)clusterPosition.X];
                    var text = string.IsNullOrWhiteSpace(description.DisplayName) ? name : description.DisplayName;
                    var textOffset = Graphics.MeasureText(text) / 2f;
                    var mapDelta = TranslateGridDeltaToMapDelta(clusterPosition - playerPosition, playerHeight + clusterHeight);
                    var mapPos = mapCenter + mapDelta;
                    if (Settings.PathfindingSettings.EnableTargetNameBackground)
                        DrawBox(mapPos - textOffset, mapPos + textOffset, Color.Black);
                    DrawText(text, mapPos - textOffset, Color.FromRgba(color.ToRgba()));
                }
            }
        }
    }
}