using System;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;

namespace Mapbox.BaseModule.Data.DataFetchers
{
    [Serializable]
    public class TerrainData : RasterData
    {
        [HideInInspector] public float[] ElevationValues;
        public bool IsElevationDataReady = false;

        // Flipped to true by Dispose(). Async readback paths can complete after the
        // owning TerrainData has been evicted; they check this flag before assigning
        // and return the rented buffer to the pool instead of leaking it.
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Fires whenever <see cref="SetElevationValues(float[])"/> or
        /// <see cref="SetElevationValues(float[],float,float)"/> completes. Multiple
        /// subscribers can safely attach with <c>+=</c> and detach with <c>-=</c>; the
        /// former single-setter <c>SetElevationChangedCallback</c> method silently wiped
        /// other subscribers, so it was removed.
        /// </summary>
        public event Action ElevationValuesUpdated;

        public float MinElevation = 0;
        public float MaxElevation = 0;

        /// <summary>
        /// True as soon as the raster <see cref="RasterData.Texture"/> has been assigned,
        /// regardless of whether the per-pixel float[] has been decoded yet. Shader
        /// elevation rendering only needs the texture and can show a tile the moment this
        /// flips true; CPU consumers (collider builder, QueryElevation APIs, CPU elevation
        /// mode) should wait for <see cref="IsElevationDataReady"/> instead.
        /// </summary>
        public bool IsTextureReady => Texture != null;

        public override void Clear()
        {
            base.Clear();
            IsElevationDataReady = false;
        }

        /// <summary>
        /// Returns the rented <see cref="ElevationValues"/> array to
        /// <see cref="ElevationArrayPool"/> when the cache evicts this entry, then runs
        /// the standard <see cref="MapboxTileData"/> dispose callback. After this call
        /// <c>ElevationValues</c> is null; nothing should hold a reference past dispose.
        /// </summary>
        public override void Dispose()
        {
            // Explicit idempotency guard. Today the ElevationValues null-check below
            // also prevents a double Return-to-pool, but the AsyncExtract late-callback
            // can reassign ElevationValues after Dispose (its IsDisposed check returns
            // the buffer to the pool, but a subsequent second Dispose without this guard
            // would still see IsElevationDataReady etc. flicker). Cheap defensive bail.
            if (IsDisposed) return;
            if (ElevationValues != null)
            {
                ElevationArrayPool.Return(ElevationValues);
                ElevationValues = null;
            }
            IsElevationDataReady = false;
            IsDisposed = true;
            base.Dispose();
        }

        // Cached side length of the square heightmap (sqrt(Length)). Computed once
        // on Set to avoid per-call Mathf.Sqrt in QueryHeightData / ReadElevation.
        private int _cachedWidth;

        public void SetElevationValues(float[] elevationArray)
        {
            ElevationValues = elevationArray;
            _cachedWidth = elevationArray != null ? (int)Mathf.Sqrt(elevationArray.Length) : 0;
            IsElevationDataReady = true;
            ElevationValuesUpdated?.Invoke();
        }

        public void SetElevationValues(float[] elevationArray, float min, float max)
        {
            ElevationValues = elevationArray;
            _cachedWidth = elevationArray != null ? (int)Mathf.Sqrt(elevationArray.Length) : 0;
            IsElevationDataReady = true;
            MinElevation = min;
            MaxElevation = max;
            ElevationValuesUpdated?.Invoke();
        }
        
        public float QueryHeightData(CanonicalTileId requestingSubTileId, float x, float y)
        {
            if (ElevationValues?.Length > 0)
            {
                var _terrainTextureScaleOffset = requestingSubTileId.CalculateScaleOffsetAtZoom(TileId.Z);
                return ReadElevation(x, y, _terrainTextureScaleOffset);
            }
            return 0;
        }
        
        public float QueryHeightData(Vector2 point)
        {
            // Mirror the 3-arg overload's guard: shader-only mode (ExtractCpuElevationData=false)
            // leaves ElevationValues null and _cachedWidth=0.
            if (!(ElevationValues?.Length > 0)) return 0;
            return ReadElevation(point.x, point.y, new Vector4(1, 1, 0, 0));
        }

        public float QueryHeightData(float x, float y)
        {
            if (!(ElevationValues?.Length > 0)) return 0;
            return ReadElevation(x, y, new Vector4(1, 1, 0, 0));
        }

        private float ReadElevation(float x, float y, Vector4 terrainTextureScaleOffset)
        {
            var width = _cachedWidth;
            var xx = TerrainRasterCoordinates.ToSampleCoordinate(Mathf.Clamp01(x), terrainTextureScaleOffset.x, terrainTextureScaleOffset.z, width);
            var yy = TerrainRasterCoordinates.ToSampleCoordinate(Mathf.Clamp01(y), terrainTextureScaleOffset.y, terrainTextureScaleOffset.w, width);

            // Bilinear sample — when the caller's UV resolution is finer than the data
            // texture's sub-region (typical for render tiles sharing one data tile with 15
            // neighbors), nearest-neighbor sampling produces visible stepping.
            var x0 = (int)xx;
            var y0 = (int)yy;
            var x1 = x0 + 1; if (x1 >= width) x1 = width - 1;
            var y1 = y0 + 1; if (y1 >= width) y1 = width - 1;
            var fx = xx - x0;
            var fy = yy - y0;

            var row0 = y0 * width;
            var row1 = y1 * width;
            var h00 = ElevationValues[row0 + x0];
            var h10 = ElevationValues[row0 + x1];
            var h01 = ElevationValues[row1 + x0];
            var h11 = ElevationValues[row1 + x1];
            var h0 = h00 + (h10 - h00) * fx;
            var h1 = h01 + (h11 - h01) * fx;
            return h0 + (h1 - h0) * fy;
        }

        
    }
}
