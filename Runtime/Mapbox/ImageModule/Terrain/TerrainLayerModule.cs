using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Interfaces;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using Mapbox.ImageModule.Terrain.TerrainStrategies;
using Mapbox.UnityMapService.DataSources;
using UnityEngine;
using TerrainData = Mapbox.BaseModule.Data.DataFetchers.TerrainData;

namespace Mapbox.ImageModule.Terrain
{
    /// <summary>
    /// Terrain layer module. Orchestrates loading of terrain-RGB raster tiles from a
    /// <see cref="Source{TerrainData}"/>, configures CPU elevation extraction based on
    /// settings, and hands each tile through a <see cref="TerrainStrategy"/> that produces
    /// render + optional collider meshes.
    /// </summary>
    public class TerrainLayerModule : ITerrainLayerModule, ITileLifecycleObserver
    {
        private TerrainLayerModuleSettings _settings;
        private Source<TerrainData> _rasterSource;
        private HashSet<CanonicalTileId> _retainedTerrainTiles;
        private TerrainStrategy _terrainStrategy;
        // Owns the Min/MaxElevation bookkeeping that used to live in MapboxMapVisualizer.
        // Constructed in AttachToMapVisualizer once the visualizer is available, disposed
        // in OnDestroy. The visualizer never references this directly.
        private TerrainBoundsTracker _boundsTracker;
        private ITileLifecycleSource _tileLifecycleSource;
        private IReadOnlyDictionary<UnwrappedTileId, UnityMapTile> _activeTiles;
        private readonly HashSet<UnwrappedTileId> _tilesPendingRestitch = new HashSet<UnwrappedTileId>();
        private Coroutine _restitchCoroutine;
        private bool _destroyed;
        // One-shot guard so QueryElevation doesn't spam the console on every call when the
        // user genuinely disabled CPU extraction.
        private bool _elevationDisabledWarningLogged;
        // Reused across GetDataId(IEnumerable) calls to avoid allocating a fresh HashSet
        // plus iterator chain from Where/Select/Distinct on every tile-cover update.
        private readonly HashSet<CanonicalTileId> _dataIdScratch = new HashSet<CanonicalTileId>();
        private Coroutine _seamReadinessCoroutine;
        private readonly Dictionary<CanonicalTileId, TerrainData> _seamReadyData = new Dictionary<CanonicalTileId, TerrainData>();
        private readonly Dictionary<CanonicalTileId, TerrainData> _seamReadyDataScratch = new Dictionary<CanonicalTileId, TerrainData>();
        // Read and written from both the main thread (elevated mesh build during tile
        // registration) and TaskManager worker threads (vector mesh-gen snapping calls
        // QueryElevation off-thread), so this cache must tolerate concurrent access.
        // A plain Dictionary corrupted under concurrent insert/remove during map load
        // (InvalidOperationException from Dictionary.FindEntry).
        private readonly ConcurrentDictionary<CanonicalTileId, TerrainData> _readyTerrainSampleCache = new ConcurrentDictionary<CanonicalTileId, TerrainData>();

        public void AttachToMapVisualizer(ITileLifecycleSource source)
        {
            // Idempotent: if a tracker is already running (re-init scenarios), keep it.
            // Constructing a second one would leak the first (coroutine + subscriptions).
            if (_boundsTracker != null) return;
            _boundsTracker = new TerrainBoundsTracker(source);
            _tileLifecycleSource = source;
            _activeTiles = source.ActiveTiles;
            source.TileUnloading += OnTileUnloading;
        }

        public TerrainLayerModule(Source<TerrainData> source, TerrainLayerModuleSettings settings) : base()
        {
            _settings = settings;
            _retainedTerrainTiles = new HashSet<CanonicalTileId>();
            _rasterSource = source;
            _terrainStrategy = new ElevatedTerrainStrategy();
        }

        public virtual IEnumerator Initialize()
        {
            yield return _rasterSource.Initialize();
            if (_rasterSource is TerrainSource terrainSource)
            {
                terrainSource.ExtractCpuElevationData = _settings.NeedsCpuElevation;
            }
            if (!_settings.ExtractCpuElevationData && _settings.NeedsCpuElevation)
            {
                var reason = !_settings.UseShaderTerrain
                    ? "UseShaderTerrain is off (CPU-elevation rendering needs the data)"
                    : "colliderOptions.addCollider is on (terrain collider needs the data)";
                Debug.LogWarning($"[Mapbox] TerrainLayerModuleSettings.ExtractCpuElevationData is unchecked, but {reason}. Extraction has been force-enabled; the checkbox has no effect in this configuration.");
            }
            _terrainStrategy.Initialize(_settings.ElevationLayerProperties);
            if (_settings.LoadBackgroundTextures)
            {
                _rasterSource?.DownloadAndCacheBaseTiles();
            }
        }

        public virtual void LoadTempTile(UnityMapTile unityTile)
        {
            if (IsZinSupportedRange(unityTile.CanonicalTileId.Z) == false)
            {
                unityTile.TerrainContainer.DisableTerrain();
                unityTile.SetCanDisplay(true);
                return;
            }

            // A pooled tile can still hold the previous occupant's CPU mesh or shader
            // properties. Keep it out of view until this tile ID gets valid terrain.
            unityTile.SetCanDisplay(false);
            var targetTileId = GetDataId(unityTile.CanonicalTileId);
            var parentTileId = targetTileId;
            for (int i = parentTileId.Z; i >= 2; i--)
            {
                parentTileId.MoveToParent();
                if (_rasterSource.GetInstantData(parentTileId, out var instantData) && IsTileReady(instantData))
                {
                    unityTile.TerrainContainer.SetTerrainData(instantData, _settings.UseShaderTerrain, TileContainerState.Temporary);
                    RegisterTerrainTile(unityTile);
                    unityTile.SetCanDisplay(true);
                    return;
                }
            }

            // Clear the old occupant's texture/subscriptions too. The display gate above
            // protects CPU meshes, which DisableTerrain cannot regenerate by itself.
            unityTile.TerrainContainer.DisableTerrain();
        }

        public virtual bool LoadInstant(UnityMapTile unityTile)
        {
            if (IsZinSupportedRange(unityTile.CanonicalTileId.Z) == false)
            {
                unityTile.TerrainContainer.DisableTerrain();
                unityTile.SetCanDisplay(true);
                return true;
            }

            var targetTileId = GetDataId(unityTile.CanonicalTileId);
            if (_rasterSource.GetInstantData(targetTileId, out var instantData) && IsTileReady(instantData))
            {
                unityTile.TerrainContainer.SetTerrainData(instantData, _settings.UseShaderTerrain, TileContainerState.Final);
                RegisterTerrainTile(unityTile);
                unityTile.SetCanDisplay(true);
                return true;
            }

            return false;
        }

        private void RegisterTerrainTile(UnityMapTile tile)
        {
            var container = tile.TerrainContainer;
            if (UsesCanonicalTerrainRgbSampling && container.State == TileContainerState.Final &&
                container.TerrainData?.TileId.Z == _settings.DataSettings.ClampDataLevelToMax &&
                TerrainRgbCanonicalSampler.IsReadyRaster(container.TerrainData))
            {
                container.SetCanonicalElevationSampler(SampleCanonicalTerrainRgb);
            }
            else container.SetCanonicalElevationSampler(null);
            if (_terrainStrategy is ElevatedTerrainStrategy elevatedStrategy && !_settings.UseShaderTerrain)
            {
                elevatedStrategy.RegisterTileWithNeighbors(tile, true, _activeTiles);
                return;
            }

            _terrainStrategy.RegisterTile(tile, !_settings.UseShaderTerrain);
        }

        /// <summary>
        /// Readiness gate used by <see cref="LoadInstant"/> and <see cref="LoadTempTile"/>.
        /// Shader-elevation rendering only needs the raster texture and can display a tile
        /// as soon as the texture exists. CPU-elevation rendering still needs the per-pixel
        /// <c>float[]</c> before displacing verts, so it must wait for extraction to finish.
        /// </summary>
        private bool IsTileReady(TerrainData data)
        {
            return _settings.UseShaderTerrain ? data.IsTextureReady : data.IsElevationDataReady;
        }

        public virtual bool RetainTiles(HashSet<CanonicalTileId> retainedTiles)
        {
            var isReady = true;
            _retainedTerrainTiles.Clear();
            foreach (var tileId in retainedTiles)
            {
                var dataId = GetDataId(tileId);
                _retainedTerrainTiles.Add(dataId);
                if (UsesCanonicalTerrainRgbSampling && dataId.Z == _settings.DataSettings.ClampDataLevelToMax)
                    AddSeamSupport(tileId, dataId, _retainedTerrainTiles);
            }

            isReady = _rasterSource.RetainTiles(_retainedTerrainTiles);
            _readyTerrainSampleCache.Clear();
            if (UsesCanonicalTerrainRgbSampling && _seamReadinessCoroutine == null && Runnable.Instance != null)
                _seamReadinessCoroutine = Runnable.Instance.StartCoroutine(WatchSeamDataReadiness());
            return isReady;
        }

        public void UpdatePositioning(IMapInformation mapInfo)
        {

        }

        public void OnDestroy()
        {
            _destroyed = true;
            if (_tileLifecycleSource != null)
            {
                _tileLifecycleSource.TileUnloading -= OnTileUnloading;
                _tileLifecycleSource = null;
            }
            if (_restitchCoroutine != null && Runnable.Instance != null)
            {
                Runnable.Instance.StopCoroutine(_restitchCoroutine);
                _restitchCoroutine = null;
            }
            _tilesPendingRestitch.Clear();
            _seamReadyData.Clear();
            _seamReadyDataScratch.Clear();
            _readyTerrainSampleCache.Clear();
            if (_seamReadinessCoroutine != null && Runnable.Instance != null)
            {
                Runnable.Instance.StopCoroutine(_seamReadinessCoroutine);
                _seamReadinessCoroutine = null;
            }
            _boundsTracker?.Dispose();
            _boundsTracker = null;
            _rasterSource.OnDestroy();
            _terrainStrategy?.OnDestroy();
        }

        private void OnTileUnloading(UnityMapTile tile)
        {
            if (_destroyed || _settings.UseShaderTerrain || tile == null)
            {
                return;
            }

            var tileId = tile.UnwrappedTileId;
            _tilesPendingRestitch.Add(tileId.North);
            _tilesPendingRestitch.Add(tileId.South);
            _tilesPendingRestitch.Add(tileId.East);
            _tilesPendingRestitch.Add(tileId.West);
            _tilesPendingRestitch.Add(tileId.NorthEast);
            _tilesPendingRestitch.Add(tileId.NorthWest);
            _tilesPendingRestitch.Add(tileId.SouthEast);
            _tilesPendingRestitch.Add(tileId.SouthWest);

            if (_restitchCoroutine != null)
            {
                return;
            }

            if (Runnable.Instance == null)
            {
                Debug.LogWarning("TerrainLayerModule: cannot restitch neighboring tiles after unload because Runnable.Instance is unavailable.");
                _tilesPendingRestitch.Clear();
                return;
            }

            _restitchCoroutine = Runnable.Instance.StartCoroutine(RestitchAfterTileUnload());
        }

        private IEnumerator RestitchAfterTileUnload()
        {
            // TileUnloading fires before the visualizer removes the tile from ActiveTiles.
            // Wait one frame so all tiles in this unload batch have been removed first.
            yield return null;

            var pendingTiles = _tilesPendingRestitch.ToArray();
            _tilesPendingRestitch.Clear();
            if (!_destroyed && _terrainStrategy is ElevatedTerrainStrategy elevatedStrategy && _activeTiles != null)
            {
                foreach (var tileId in pendingTiles)
                {
                    if (_activeTiles.TryGetValue(tileId, out var activeTile))
                    {
                        elevatedStrategy.RestitchTileWithNeighbors(activeTile, _activeTiles);
                    }
                }
            }

            _restitchCoroutine = null;
        }

        //COROUTINE METHODS only used in initialization so far
        #region coroutine methods
        public virtual IEnumerator LoadTileData(CanonicalTileId tileId, Action<TerrainData> callback = null)
        {
            return _rasterSource.LoadTileCoroutine(tileId, callback);
        }

        public virtual IEnumerator LoadTiles(IEnumerable<CanonicalTileId> tiles)
        {
            // Materialize before passing to the coroutine: the IEnumerable returned by
            // GetDataId is the shared _dataIdScratch set. If LoadTilesCoroutine consumes
            // lazily and another GetDataId call clears the scratch before it finishes,
            // the consumer would see the wrong contents.
            var materialized = new List<CanonicalTileId>(GetDataId(tiles));
            yield return _rasterSource.LoadTilesCoroutine(materialized);
        }

        public IEnumerable<IEnumerator> GetTileCoverCoroutines(IEnumerable<CanonicalTileId> tiles)
        {
            // Same materialization rationale: callers iterate this lazily downstream and
            // would otherwise read a mutated _dataIdScratch after another GetDataId call.
            var inputTiles = new List<CanonicalTileId>(tiles);
            var materialized = new List<CanonicalTileId>(GetDataId(inputTiles));
            // Debug.Log($"[TILE-DIAG][terrain-cover] input=[{string.Join(",", inputTiles)}] dataIds=[{string.Join(",", materialized)}]");
            // Explicit lambda (not method-group): LoadTileData's optional callback
            // parameter makes the method-group conversion ambiguous between Select's
            // two overloads (Func<T,R> vs Func<T,int,R>).
            return materialized.Select(t => LoadTileData(t)).Where(x => x != null);
        }
        #endregion


        //PRIVATE METHODS
        private bool IsZinSupportedRange(int targetZ)
        {
            return _settings.RejectTilesOutsideZoom.x <= targetZ && _settings.RejectTilesOutsideZoom.y >= targetZ;
        }

        private CanonicalTileId GetDataId(CanonicalTileId tileId)
        {
            return tileId.ParentAt(GetDataZoom(tileId.Z));
        }

        private int GetDataZoom(int renderZoom)
        {
            var maxZoom = _settings.DataSettings.ClampDataLevelToMax;
            var targetZoom = (int)Mathf.Max(renderZoom - 2, _settings.RejectTilesOutsideZoom.x);
            return targetZoom >= maxZoom ? maxZoom : targetZoom;
        }

        private bool UsesCanonicalTerrainRgbSampling =>
            !_settings.UseShaderTerrain &&
            _settings.DataSettings.TilesetId == "mapbox.terrain-rgb";

        private static void AddSeamSupport(CanonicalTileId renderTile, CanonicalTileId center,
            HashSet<CanonicalTileId> destination)
        {
            if (renderTile.Z < center.Z) return;
            var worldSize = 1 << center.Z;
            var zoomDiff = renderTile.Z - center.Z;
            var childCount = 1 << zoomDiff;
            // Retain support whenever this render tile covers the final source
            // pixel band. With render zooms more than eight levels above the data
            // grid, that band spans multiple render tiles.
            var samplesPerChild = 256d / childCount;
            var childX = renderTile.X % childCount;
            var childY = renderTile.Y % childCount;
            var needsEast = (childX + 1d) * samplesPerChild > 255d;
            var needsSouth = (childY + 1d) * samplesPerChild > 255d;
            if (needsEast) destination.Add(CanonicalTileId.FromUnwrappedValues(center.Z, center.X + 1, center.Y));
            if (needsSouth && center.Y + 1 < worldSize)
                destination.Add(CanonicalTileId.FromUnwrappedValues(center.Z, center.X, center.Y + 1));
            if (needsEast && needsSouth && center.Y + 1 < worldSize)
                destination.Add(CanonicalTileId.FromUnwrappedValues(center.Z, center.X + 1, center.Y + 1));
        }

        private IEnumerator WatchSeamDataReadiness()
        {
            while (!_destroyed)
            {
                _seamReadyDataScratch.Clear();
                foreach (var id in _retainedTerrainTiles)
                {
                    if (!_rasterSource.GetInstantData(id, out var data) || !data.IsElevationDataReady) continue;
                    _seamReadyDataScratch[id] = data;
                }
                var dataSetChanged = _seamReadyDataScratch.Count != _seamReadyData.Count;
                if (!dataSetChanged)
                {
                    foreach (var pair in _seamReadyDataScratch)
                    {
                        if (!_seamReadyData.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(previous, pair.Value))
                        {
                            dataSetChanged = true;
                            break;
                        }
                    }
                }
                if (dataSetChanged)
                {
                    _seamReadyData.Clear();
                    _readyTerrainSampleCache.Clear();
                    foreach (var pair in _seamReadyDataScratch) _seamReadyData[pair.Key] = pair.Value;
                    if (_activeTiles != null && _terrainStrategy is ElevatedTerrainStrategy strategy)
                    {
                        foreach (var tile in _activeTiles.Values)
                        {
                            if (tile != null && tile.CanDisplay && tile.TerrainContainer != null &&
                                tile.TerrainContainer.State == TileContainerState.Final)
                                strategy.RebuildTileWithCanonicalSampling(tile, _activeTiles);
                        }
                    }
                }
                yield return new WaitForSeconds(0.25f);
            }
            _seamReadinessCoroutine = null;
        }

        private float? SampleCanonicalTerrainRgb(CanonicalTileId renderTileId, float u, float v)
        {
            var dataZoom = _settings.DataSettings.ClampDataLevelToMax;
            if (!UsesCanonicalTerrainRgbSampling || GetDataZoom(renderTileId.Z) != dataZoom ||
                !TerrainRgbCanonicalSampler.TrySample(renderTileId, dataZoom, u, v, GetReadyTerrainData, out var height))
                return null;
            return height;
        }

        private TerrainData GetReadyTerrainData(CanonicalTileId id)
        {
            if (_readyTerrainSampleCache.TryGetValue(id, out var cached))
            {
                if (cached != null && cached.IsElevationDataReady && cached.ElevationValues != null) return cached;
                // ConcurrentDictionary has no Remove; TryRemove racing with another
                // thread's reinsert is benign — both insert equivalent data for the id.
                _readyTerrainSampleCache.TryRemove(id, out _);
            }
            if (!_rasterSource.GetInstantData(id, out var data) || !data.IsElevationDataReady || data.ElevationValues == null)
                return null;
            _readyTerrainSampleCache[id] = data;
            return data;
        }


        //API

        /// <summary>
        /// This method will only search the memory cache for available data.
        /// </summary>
        /// <param name="tileId">TileId of the elevation data</param>
        /// <param name="x">Point in texture coordinate space, origin is bottom left.</param>
        /// <param name="y">Point in texture coordinate space, origin is bottom left.</param>
        /// <returns></returns>
        public float QueryElevation(CanonicalTileId tileId, float x, float y)
        {
            if (!_settings.NeedsCpuElevation && !_elevationDisabledWarningLogged)
            {
                _elevationDisabledWarningLogged = true;
                Debug.LogWarning("[Mapbox] QueryElevation was called but TerrainLayerModuleSettings.ExtractCpuElevationData is disabled, so no CPU elevation data exists. Returning 0 for every query. Enable ExtractCpuElevationData if anything in your scene snaps to terrain (vector features, buildings, colliders, custom elevation lookups).");
            }

            if (UsesCanonicalTerrainRgbSampling)
            {
                var canonicalHeight = SampleCanonicalTerrainRgb(tileId, x, y);
                if (canonicalHeight.HasValue) return canonicalHeight.Value;
            }

            var originalTileId = tileId;
            var targetTileId = tileId;
            for (int i = 0; i < 5; i++)
            {
                if (_rasterSource.GetInstantData(targetTileId, out var instantData))
                {
                    return instantData.QueryHeightData(originalTileId, x, y);
                }
                targetTileId.MoveToParent();
            }

            return 0;
        }

        /// <summary>
        /// Maps a set of render tile ids to the distinct data tile ids they sample.
        /// </summary>
        /// <remarks>
        /// The returned enumerable is a reusable per-instance scratch set — the caller
        /// MUST finish iterating before invoking <see cref="GetDataId(IEnumerable{CanonicalTileId})"/>
        /// again (the next call clears the same set). Do not retain a reference past the
        /// loop body.
        /// </remarks>
        public IEnumerable<CanonicalTileId> GetDataId(IEnumerable<CanonicalTileId> tileIdList)
        {
            // With the data-zoom offset of 2, up to 16 render tile ids collapse to the
            // same data id. Reuse the scratch set to avoid allocating a HashSet plus
            // Where/Select/Distinct iterator chain per call.
            _dataIdScratch.Clear();
            foreach (var tileId in tileIdList)
            {
                if (!IsZinSupportedRange(tileId.Z))
                {
                    continue;
                }
                _dataIdScratch.Add(GetDataId(tileId));
            }
            return _dataIdScratch;
        }

        /// <summary>
        /// This method will cause tile requests if data isn't already available on memory cache. Use with caution.
        /// </summary>
        /// <param name="latLng">Latitude longitude of the location you want to query</param>
        /// <param name="callback">Callback method returning the elevation in float</param>
        /// <returns></returns>
        public IEnumerator GetElevationData(LatitudeLongitude latLng, Action<float> callback = null)
        {
            var tileId = Conversions.LatitudeLongitudeToTileId(latLng, 14).Canonical;
            var dataFound = false;
            for (int i = 14; i >= 2; i--)
            {
                if (_rasterSource.GetInstantData(tileId, out var instantData))
                {
                    var tilePos = Conversions.LatitudeLongitudeToInTile01(latLng, instantData.TileId);
                    var elevation = instantData.QueryHeightData(tilePos);
                    callback?.Invoke(elevation);
                    dataFound = true;
                    break;
                }
                else
                {
                    tileId = tileId.GetParentTileId;
                }
            }

            if (!dataFound)
            {
                tileId = Conversions.LatitudeLongitudeToTileId(latLng, 14).Canonical;
                yield return LoadTileData(tileId, terrainData =>
                {
                    var tilePos = Conversions.LatitudeLongitudeToInTile01(latLng, terrainData.TileId);
                    var elevation = terrainData.QueryHeightData(tilePos);
                    callback?.Invoke(elevation);
                });
            }
        }
    }
}
