using System;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tiles;

namespace Mapbox.ImageModule.Terrain
{
    /// <summary>Samples one fixed-zoom Terrain-RGB grid across parent raster boundaries.</summary>
    internal static class TerrainRgbCanonicalSampler
    {
        private const int RasterWidth = 256;

        public static bool IsReadyRaster(TerrainData data) =>
            data != null && data.IsElevationDataReady &&
            data.ElevationValues?.Length == RasterWidth * RasterWidth;

        public static bool TrySample(CanonicalTileId renderTile, int dataZoom, float u, float v,
            Func<CanonicalTileId, TerrainData> getReadyData, out float height)
        {
            height = 0f;
            if (renderTile.Z < dataZoom || dataZoom < 0 || dataZoom > 30 || getReadyData == null) return false;

            // Mesh UV v grows northward; XYZ raster rows grow southward. Keep the
            // integer tile origin separate from the local fraction for float precision.
            var samplesPerRenderTile = RasterWidth / Math.Pow(2d, renderTile.Z - dataZoom);
            var globalX = (double)renderTile.X * samplesPerRenderTile + Clamp01(u) * samplesPerRenderTile;
            var globalY = (double)renderTile.Y * samplesPerRenderTile + (1d - Clamp01(v)) * samplesPerRenderTile;
            var worldSamples = (1L << dataZoom) * RasterWidth;
            if (globalY > worldSamples - 1d) globalY = worldSamples - 1d;

            var x0 = (long)Math.Floor(globalX);
            var y0 = (long)Math.Floor(globalY);
            var fx = (float)(globalX - x0);
            var fy = (float)(globalY - y0);
            if (!TryRead(x0, y0, dataZoom, getReadyData, out var h00)) return false;
            var h10 = h00;
            var h01 = h00;
            var h11 = h00;
            if (fx != 0f && !TryRead(x0 + 1, y0, dataZoom, getReadyData, out h10)) return false;
            if (fy != 0f && !TryRead(x0, y0 + 1, dataZoom, getReadyData, out h01)) return false;
            if (fx != 0f && fy != 0f && !TryRead(x0 + 1, y0 + 1, dataZoom, getReadyData, out h11)) return false;
            if (fx == 0f) { h10 = h00; h11 = h01; }
            if (fy == 0f) { h01 = h00; h11 = h10; }

            var top = h00 + (h10 - h00) * fx;
            var bottom = h01 + (h11 - h01) * fx;
            height = top + (bottom - top) * fy;
            return true;
        }

        private static bool TryRead(long globalX, long globalY, int dataZoom,
            Func<CanonicalTileId, TerrainData> getReadyData, out float height)
        {
            var world = 1 << dataZoom;
            var tileXUnwrapped = (long)Math.Floor(globalX / (double)RasterWidth);
            var tileY = (int)Math.Floor(globalY / (double)RasterWidth);
            if (tileY < 0 || tileY >= world)
            {
                height = 0f;
                return false;
            }

            var tileX = (int)(((tileXUnwrapped % world) + world) % world);
            var localX = (int)(globalX - tileXUnwrapped * RasterWidth);
            var localTopDownY = (int)(globalY - (long)tileY * RasterWidth);
            var localBottomUpY = RasterWidth - 1 - localTopDownY;
            if (TryReadExact(tileX, tileY, localX, localBottomUpY, dataZoom, getReadyData, out height)) return true;

            // If the owning east/south tile has not arrived yet, use the closest
            // touching pixel that is already available. Candidate order is stable:
            // west edge, north edge, then the northwest corner.
            if (localX == 0 &&
                TryReadExact(tileX - 1, tileY, RasterWidth - 1, localBottomUpY, dataZoom, getReadyData, out height)) return true;
            if (localTopDownY == 0 && tileY > 0 &&
                TryReadExact(tileX, tileY - 1, localX, 0, dataZoom, getReadyData, out height)) return true;
            if (localX == 0 && localTopDownY == 0 && tileY > 0 &&
                TryReadExact(tileX - 1, tileY - 1, RasterWidth - 1, 0, dataZoom, getReadyData, out height)) return true;

            height = 0f;
            return false;
        }

        private static bool TryReadExact(int tileX, int tileY, int localX, int localBottomUpY, int dataZoom,
            Func<CanonicalTileId, TerrainData> getReadyData, out float height)
        {
            var world = 1 << dataZoom;
            if (tileY < 0 || tileY >= world)
            {
                height = 0f;
                return false;
            }
            tileX = ((tileX % world) + world) % world;
            var data = getReadyData(new CanonicalTileId(dataZoom, tileX, tileY));
            if (!IsReadyRaster(data))
            {
                height = 0f;
                return false;
            }

            height = data.ElevationValues[localBottomUpY * RasterWidth + localX];
            return true;
        }

        private static double Clamp01(float value) => value < 0f ? 0d : value > 1f ? 1d : value;
    }
}
