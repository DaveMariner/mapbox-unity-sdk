namespace Mapbox.BaseModule.Data.DataFetchers
{
    /// <summary>Maps tile-local UVs to the registered sample domain of a raster.</summary>
    internal static class TerrainRasterCoordinates
    {
        public static float ToSampleCoordinate(float local, float scale, float offset, int width)
        {
            // Raster samples span the normalized endpoints [0, 1] across W - 1 intervals.
            var normalized = offset + local * scale;
            var coordinate = normalized * (width - 1);
            if (coordinate < 0f) return 0f;
            var max = width - 1;
            return coordinate > max ? max : coordinate;
        }
    }
}
