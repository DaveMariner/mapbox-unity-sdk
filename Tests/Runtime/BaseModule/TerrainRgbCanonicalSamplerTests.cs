using System.Collections.Generic;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.ImageModule.Terrain;
using NUnit.Framework;

namespace Mapbox.BaseModuleTests
{
    public class TerrainRgbCanonicalSamplerTests
    {
        [TestCase(13, 17)]
        [TestCase(14, 18)]
        [TestCase(15, 20)]
        public void AdjacentRenderTilesResolveSharedParentEdgeFromOneCanonicalSample(int dataZoom, int renderZoom)
        {
            var parent = new CanonicalTileId(dataZoom, 1 << (dataZoom - 1), 1 << (dataZoom - 1));
            var data = new Dictionary<CanonicalTileId, TerrainData>();
            AddData(data, parent);
            AddData(data, new CanonicalTileId(dataZoom, parent.X + 1, parent.Y));
            var childCount = 1 << (renderZoom - dataZoom);
            var scale = 256f / childCount;
            var left = new CanonicalTileId(renderZoom, parent.X * childCount + childCount - 1, parent.Y * childCount + 4);
            var right = new CanonicalTileId(renderZoom, left.X + 1, left.Y);
            try
            {
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(left, dataZoom, 1f, 0.625f, Resolve(data), out var leftHeight));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(right, dataZoom, 0f, 0.625f, Resolve(data), out var rightHeight));
                Assert.AreEqual(leftHeight, rightHeight, 0.0001f);

                Assert.AreEqual(256f * 1.25f + 4.375f * scale * 2.5f, leftHeight, 0.001f);
            }
            finally { Dispose(data); }
        }

        [TestCase(13, 17)]
        [TestCase(14, 18)]
        [TestCase(15, 20)]
        public void FourParentCornerUsesSoutheastSampleRegardlessOfQueryingTile(int dataZoom, int renderZoom)
        {
            var parent = new CanonicalTileId(dataZoom, 1 << (dataZoom - 1), 1 << (dataZoom - 1));
            var data = new Dictionary<CanonicalTileId, TerrainData>();
            AddData(data, parent);
            AddData(data, new CanonicalTileId(dataZoom, parent.X + 1, parent.Y));
            AddData(data, new CanonicalTileId(dataZoom, parent.X, parent.Y + 1));
            AddData(data, new CanonicalTileId(dataZoom, parent.X + 1, parent.Y + 1));
            var childCount = 1 << (renderZoom - dataZoom);
            var northWest = new CanonicalTileId(renderZoom, parent.X * childCount + childCount - 1, parent.Y * childCount + childCount - 1);
            var northEast = new CanonicalTileId(renderZoom, northWest.X + 1, northWest.Y);
            var southWest = new CanonicalTileId(renderZoom, northWest.X, northWest.Y + 1);
            var southEast = new CanonicalTileId(renderZoom, northWest.X + 1, northWest.Y + 1);
            try
            {
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northWest, dataZoom, 1f, 0f, Resolve(data), out var a));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northEast, dataZoom, 0f, 0f, Resolve(data), out var b));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southWest, dataZoom, 1f, 1f, Resolve(data), out var c));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southEast, dataZoom, 0f, 1f, Resolve(data), out var d));
                Assert.AreEqual(a, b, 0.0001f);
                Assert.AreEqual(a, c, 0.0001f);
                Assert.AreEqual(a, d, 0.0001f);
                Assert.AreEqual(256f * 1.25f + 256f * 2.5f, a, 0.001f);

                // One query 0.25 samples inside the NW raster corner uses four
                // nonzero weights spanning all four parent rasters.
                var samplesPerTile = 256f / childCount;
                var nearCornerOffset = 0.25f / samplesPerTile;
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northWest, dataZoom, 1f - nearCornerOffset, nearCornerOffset,
                    Resolve(data), out var nearCorner));
                Assert.AreEqual(255.75f * 1.25f + 255.75f * 2.5f, nearCorner, 0.002f);
            }
            finally { Dispose(data); }
        }

        [Test]
        [TestCase(13, 17)]
        [TestCase(14, 18)]
        [TestCase(15, 20)]
        public void MissingNeighborUsesStableClosestPixelThenSwitchesWhenOwnerArrives(int dataZoom, int renderZoom)
        {
            var parent = new CanonicalTileId(dataZoom, 1 << (dataZoom - 1), 1 << (dataZoom - 1));
            var data = new Dictionary<CanonicalTileId, TerrainData>();
            AddData(data, parent);
            var childCount = 1 << (renderZoom - dataZoom);
            var edgeTile = new CanonicalTileId(renderZoom, parent.X * childCount + childCount - 1, parent.Y * childCount + 4);
            try
            {
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(edgeTile, dataZoom, 1f, 0.5f, Resolve(data), out var fallback));
                var east = new CanonicalTileId(dataZoom, parent.X + 1, parent.Y);
                AddData(data, east);
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(edgeTile, dataZoom, 1f, 0.5f, Resolve(data), out var canonical));
                Assert.AreNotEqual(fallback, canonical);
            }
            finally { Dispose(data); }
        }

        [Test]
        [TestCase(13, 17)]
        [TestCase(14, 18)]
        [TestCase(15, 20)]
        public void MissingSoutheastOwnerUsesSameCornerFallbackFromAllFourTiles(int dataZoom, int renderZoom)
        {
            var parent = new CanonicalTileId(dataZoom, 1 << (dataZoom - 1), 1 << (dataZoom - 1));
            var data = new Dictionary<CanonicalTileId, TerrainData>();
            AddData(data, parent);
            AddData(data, new CanonicalTileId(dataZoom, parent.X + 1, parent.Y));
            AddData(data, new CanonicalTileId(dataZoom, parent.X, parent.Y + 1));
            var childCount = 1 << (renderZoom - dataZoom);
            var northWest = new CanonicalTileId(renderZoom, parent.X * childCount + childCount - 1, parent.Y * childCount + childCount - 1);
            var northEast = new CanonicalTileId(renderZoom, northWest.X + 1, northWest.Y);
            var southWest = new CanonicalTileId(renderZoom, northWest.X, northWest.Y + 1);
            var southEast = new CanonicalTileId(renderZoom, northWest.X + 1, northWest.Y + 1);
            try
            {
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northWest, dataZoom, 1f, 0f, Resolve(data), out var a));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northEast, dataZoom, 0f, 0f, Resolve(data), out var b));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southWest, dataZoom, 1f, 1f, Resolve(data), out var c));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southEast, dataZoom, 0f, 1f, Resolve(data), out var d));
                Assert.AreEqual(a, b, 0.0001f);
                Assert.AreEqual(a, c, 0.0001f);
                Assert.AreEqual(a, d, 0.0001f);
                Assert.AreEqual(958.75f, a, 0.001f, "west neighbor's touching pixel is selected deterministically");

                AddData(data, new CanonicalTileId(dataZoom, parent.X + 1, parent.Y + 1));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northWest, dataZoom, 1f, 0f, Resolve(data), out var readyA));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(northEast, dataZoom, 0f, 0f, Resolve(data), out var readyB));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southWest, dataZoom, 1f, 1f, Resolve(data), out var readyC));
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(southEast, dataZoom, 0f, 1f, Resolve(data), out var readyD));
                Assert.AreEqual(readyA, readyB, 0.0001f);
                Assert.AreEqual(readyA, readyC, 0.0001f);
                Assert.AreEqual(readyA, readyD, 0.0001f);
                Assert.AreEqual(960f, readyA, 0.001f, "owning southeast sample replaces fallback after it becomes ready");
                Assert.AreNotEqual(a, readyA);
            }
            finally { Dispose(data); }
        }

        [Test]
        public void SouthWorldEdgeClampsTheWholeFinalPixelBand()
        {
            const int dataZoom = 13;
            var lastY = (1 << dataZoom) - 1;
            var tile = new CanonicalTileId(dataZoom, 1 << (dataZoom - 1), lastY);
            var data = new Dictionary<CanonicalTileId, TerrainData>();
            AddData(data, tile);
            try
            {
                Assert.IsTrue(TerrainRgbCanonicalSampler.TrySample(tile, dataZoom, 0.5f, 0.001f,
                    Resolve(data), out var height));
                var expectedX = 128f * 1.25f;
                var expectedY = (lastY - (1 << (dataZoom - 1))) * 256f + 255f;
                Assert.AreEqual(expectedX + expectedY * 2.5f, height, 0.01f);
            }
            finally { Dispose(data); }
        }

        private static System.Func<CanonicalTileId, TerrainData> Resolve(Dictionary<CanonicalTileId, TerrainData> data) =>
            id => data.TryGetValue(id, out var value) ? value : null;

        private static void AddData(Dictionary<CanonicalTileId, TerrainData> destination, CanonicalTileId id)
        {
            var values = ElevationArrayPool.Rent(256 * 256);
            for (var row = 0; row < 256; row++)
            for (var x = 0; x < 256; x++)
            {
                var topDownY = 255 - row;
                var localGlobalX = (id.X - (1 << (id.Z - 1))) * 256 + x;
                var localGlobalY = (id.Y - (1 << (id.Z - 1))) * 256 + topDownY;
                values[row * 256 + x] = localGlobalX * 1.25f + localGlobalY * 2.5f;
            }
            var terrain = new TerrainData { TileId = id };
            terrain.SetElevationValues(values);
            destination.Add(id, terrain);
        }

        private static void Dispose(Dictionary<CanonicalTileId, TerrainData> data)
        {
            foreach (var terrain in data.Values) terrain.Dispose();
        }
    }
}
