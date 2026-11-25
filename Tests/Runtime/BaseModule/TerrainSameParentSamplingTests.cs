using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Unity;
using Mapbox.ImageModule.Terrain.Settings;
using Mapbox.ImageModule.Terrain.TerrainStrategies;
using NUnit.Framework;
using UnityEngine;
using TerrainData = Mapbox.BaseModule.Data.DataFetchers.TerrainData;

namespace Mapbox.BaseModuleTests
{
    public class TerrainSameParentSamplingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SameParentMeshEdgesUseIdenticalSamplesBeforeStitching(bool skirts)
        {
            const int width = 256;
            var parent = new CanonicalTileId(14, 9132, 6231);
            var values = new float[width * width];
            for (var y = 0; y < width; y++)
                for (var x = 0; x < width; x++)
                    values[y * width + x] = x + 3 * y;

            var data = new TerrainData { TileId = parent };
            data.SetElevationValues(values);
            var settings = new ElevationLayerProperties();
            settings.modificationOptions.SimplificationFactor = 8; // 16 surface intervals
            settings.sideWallOptions.isActive = skirts;
            settings.colliderOptions.addCollider = true;
            var strategy = new ElevatedTerrainStrategy();
            strategy.Initialize(settings);

            var left = CreateTile(new CanonicalTileId(18, parent.X * 16 + 7, parent.Y * 16 + 5), data);
            var right = CreateTile(new CanonicalTileId(18, parent.X * 16 + 8, parent.Y * 16 + 5), data);
            var north = CreateTile(new CanonicalTileId(18, parent.X * 16 + 7, parent.Y * 16 + 7), data);
            var south = CreateTile(new CanonicalTileId(18, parent.X * 16 + 7, parent.Y * 16 + 8), data);
            var tiles = new[] { left, right, north, south };
            try
            {
                foreach (var tile in tiles)
                    strategy.RegisterTile(tile, true);

                var side = settings.modificationOptions.sampleCount + 1 + (skirts ? 2 : 0);
                var first = skirts ? 1 : 0;
                var last = side - 1 - first;
                var leftVertices = left.MeshFilter.sharedMesh.vertices;
                var rightVertices = right.MeshFilter.sharedMesh.vertices;
                var northVertices = north.MeshFilter.sharedMesh.vertices;
                var southVertices = south.MeshFilter.sharedMesh.vertices;
                var colliderVertices = left.GetComponent<MeshCollider>().sharedMesh.vertices;

                for (var i = 0; i <= settings.modificationOptions.sampleCount; i++)
                {
                    var local = (float)i / settings.modificationOptions.sampleCount;
                    Assert.AreEqual(left.TerrainContainer.QueryHeightData(1f, local), right.TerrainContainer.QueryHeightData(0f, local), 0.0001f);
                    Assert.AreEqual(north.TerrainContainer.QueryHeightData(local, 0f), south.TerrainContainer.QueryHeightData(local, 1f), 0.0001f);
                    Assert.AreEqual(data.QueryHeightData(left.CanonicalTileId, 1f, local), data.QueryHeightData(right.CanonicalTileId, 0f, local), 0.0001f,
                        "public TerrainData east/west query at sample " + i);
                    Assert.AreEqual(data.QueryHeightData(north.CanonicalTileId, local, 0f), data.QueryHeightData(south.CanonicalTileId, local, 1f), 0.0001f,
                        "public TerrainData north/south query at sample " + i);
                    Assert.AreEqual(leftVertices[(first + i) * side + last].y, rightVertices[(first + i) * side + first].y, 0.0001f,
                        "east/west mesh edge at sample " + i);
                    Assert.AreEqual(northVertices[first * side + first + i].y, southVertices[last * side + first + i].y, 0.0001f,
                        "north/south mesh edge at sample " + i);
                }

                var interiorX = 6;
                var interiorY = 9;
                var renderIndex = (first + interiorY) * side + first + interiorX;
                var colliderSide = settings.modificationOptions.sampleCount + 1;
                var colliderIndex = interiorY * colliderSide + interiorX;
                Assert.AreEqual(leftVertices[renderIndex].y, colliderVertices[colliderIndex].y, 0.0001f,
                    "interior collider sample must match the independently sampled render vertex");
            }
            finally
            {
                foreach (var tile in tiles)
                    if (tile != null) Object.Destroy(tile.gameObject);
                strategy.OnDestroy();
                data.Dispose();
            }
        }

        private static UnityMapTile CreateTile(CanonicalTileId id, TerrainData data)
        {
            var gameObject = new GameObject("Terrain same-parent sampling test tile");
            var tile = gameObject.AddComponent<UnityMapTile>();
            // Awake runs automatically in PlayMode; invoke it explicitly for EditMode
            // test runners that create components without lifecycle callbacks.
            if (tile.TerrainContainer == null) tile.Awake();
            tile.Initialize(new UnwrappedTileId(id), 1f);
            tile.TerrainContainer.SetTerrainData(data, false);
            return tile;
        }
    }
}
