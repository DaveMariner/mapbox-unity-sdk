using System;
using System.Collections;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Platform.Cache;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using UnityEngine;
using TerrainData = Mapbox.BaseModule.Data.DataFetchers.TerrainData;

namespace Mapbox.UnityMapService.DataSources
{
    public class TerrainSource : ImageSource<TerrainData>
    {
        protected ImageSourceSettings _settings;
        private IElevationDataExtractionStrategy _elevationDataExtractionStrategy;

        /// <summary>
        /// When <c>false</c>, the per-pixel terrain-RGB -> float[] decode pass is skipped
        /// entirely — saves ~65K pixel decodes and a ~256KB float[] per tile. The raster
        /// Texture is still populated, so shader-elevation rendering keeps working; only
        /// CPU consumers (collider builder, QueryElevation APIs, CPU-elevation mode, vector
        /// snap-to-terrain modifiers) are disabled. Defaults to <c>true</c> for back-compat;
        /// the parent <see cref="TerrainLayerModule"/> overrides this at Initialize based on
        /// its settings.
        /// </summary>
        public bool ExtractCpuElevationData { get; set; } = true;

        public TerrainSource(DataFetchingManager dataFetchingManager, MapboxCacheManager mapboxCacheManager, ImageSourceSettings settings)
            : base(dataFetchingManager, mapboxCacheManager, settings)
        {
            _settings = settings;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                _elevationDataExtractionStrategy = new AsyncExtractElevationArray();
            }
            else
            {
                _elevationDataExtractionStrategy = new SyncExtractElevationArray();
            }
        }

        public override void DownloadAndCacheBaseTiles()
        {
            var backgroundTiles = new HashSet<CanonicalTileId>();
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    backgroundTiles.Add(new CanonicalTileId(2, i, j));
                }
            }

            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    backgroundTiles.Add(new CanonicalTileId(1, i, j));
                }
            }

            backgroundTiles.Add(new CanonicalTileId(0, 0, 0));

            foreach (var tileId in backgroundTiles)
            {
                BackgroundLoad(tileId, _tilesetId);
            }
        }

        protected override RasterTile CreateTile(CanonicalTileId tileId, string tilesetId)
        {
            RasterTile rasterTile;

            // //TODO fix this obviously
            if (tilesetId == "mapbox.mapbox-terrain-dem-v1")
            {
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    rasterTile = new DemRasterTile(tileId, tilesetId, _settings.UseNonReadableTextures);
                }
                else
                {
                    rasterTile = new DemRasterTile(tileId, tilesetId, false);
                }

            }
            else
            {
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    rasterTile = new RawPngRasterTile(tileId, tilesetId, _settings.UseNonReadableTextures);
                }
                else
                {
                    rasterTile = new RawPngRasterTile(tileId, tilesetId, false);
                }
            }

            return rasterTile;
        }

        protected override TerrainData CreateRasterDataWrapper(RasterTile tile)
        {
            TerrainData rasterData = new TerrainData()
            {
                TileId = tile.Id,
                TilesetId = tile.TilesetId,
                Texture = tile.Texture2D,
                CacheType = tile.FromCache,
                Data = tile.Data,
                ETag = tile.ETag,
                ExpirationDate = tile.ExpirationDate
            };

            return rasterData;
        }

        public override IEnumerator LoadTileCoroutine(CanonicalTileId requestedDataTileId, Action<TerrainData> callback = null)
        {
            TerrainData terrainData = null;
            // Debug.Log($"[TILE-DIAG][terrain] load begin id={requestedDataTileId} cpuElevation={ExtractCpuElevationData}");
            yield return Runnable.Instance.StartCoroutine(base.LoadTileCoroutine(requestedDataTileId, (data) =>
            {
                terrainData = data;
                // Debug.Log($"[TILE-DIAG][terrain] raster callback id={requestedDataTileId} dataNull={data == null} texture={(data != null && data.Texture != null)} elevationReady={data != null && data.IsElevationDataReady}");
            }));
            if (terrainData != null && terrainData.Texture != null && ExtractCpuElevationData)
            {
                // Debug.Log($"[TILE-DIAG][terrain] elevation extraction begin id={requestedDataTileId}");
                yield return Runnable.Instance.StartCoroutine(ExtractElevationValues(terrainData));
                // Debug.Log($"[TILE-DIAG][terrain] elevation extraction end id={requestedDataTileId} elevationReady={terrainData.IsElevationDataReady}");
            }
            // Debug.Log($"[TILE-DIAG][terrain] load end id={requestedDataTileId} dataNull={terrainData == null} texture={(terrainData != null && terrainData.Texture != null)} elevationReady={terrainData != null && terrainData.IsElevationDataReady}");
            callback?.Invoke(terrainData);
        }

        protected IEnumerator ExtractElevationValues(TerrainData data)
        {
            // Use the TerrainData overload (sets MinElevation/MaxElevation as part of the
            // decode). Previously this path used Action<float[]> + the 1-arg SetElevationValues,
            // which left Min/Max at 0 — meaning RecomputeTerrainBounds never widened the
            // shared TerrainInfo for any tile loaded through LoadTileCoroutine.
            //
            // Subscribe to BOTH ElevationValuesUpdated and the dispose multicast: the async
            // GPU readback no-ops on disposed data and never raises ElevationValuesUpdated,
            // so without the dispose hook the coroutine would yield forever and pin the
            // TerrainData. Rapid pan + cache eviction during initial load is the trigger.
            var finished = false;
            Action onDone = () => finished = true;
            data.ElevationValuesUpdated += onDone;
            data.AddDisposeCallback(onDone);
            _elevationDataExtractionStrategy.ExtractHeightData(data.Texture, data);
            while (!finished) yield return null;
            data.ElevationValuesUpdated -= onDone;
            data.RemoveDisposeCallback(onDone);
        }

        protected override void TextureReceivedFromFile(TerrainData cacheItem)
        {
            base.TextureReceivedFromFile(cacheItem);
            if (ExtractCpuElevationData)
            {
                _elevationDataExtractionStrategy.ExtractHeightData(cacheItem);
            }
        }

        protected override TerrainData TextureReceivedFromWeb(RasterTile tile)
        {
            var cacheItem = base.TextureReceivedFromWeb(tile);

            if (cacheItem != null && ExtractCpuElevationData)
            {
                _elevationDataExtractionStrategy.ExtractHeightData(cacheItem);
            }

            return cacheItem;
        }
    }
}
