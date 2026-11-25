using System;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tiles;
using UnityEngine;
using TerrainData = Mapbox.BaseModule.Data.DataFetchers.TerrainData;

namespace Mapbox.BaseModule.Unity
{
    [Serializable]
    public class UnityTileTerrainContainer
    {
        public TileContainerState State = TileContainerState.Final;
        
        private Action ElevationValuesUpdated;
        private Action _onDisposeCallback;
        private const string ElevationMultiplierFieldNameID = "_ElevationMultiplier";
        private const string ElevationChangeTimerFieldNameID = "_ElevationChangeTime";
        private const string ShaderElevationTextureScaleOffsetFieldNameID = "_HeightTexture_ST";
        private const string ShaderElevationTextureFieldNameID = "_HeightTexture";
		
        private static readonly int ElevationMultiplier = Shader.PropertyToID(ElevationMultiplierFieldNameID);
        private static readonly int ElevationChangeTime = Shader.PropertyToID(ElevationChangeTimerFieldNameID);
        private static readonly int HeightTextureST = Shader.PropertyToID(ShaderElevationTextureScaleOffsetFieldNameID);
        private static readonly int HeightTexture = Shader.PropertyToID(ShaderElevationTextureFieldNameID);
        
        
        private UnityMapTile _unityMapTile;
        [SerializeField] public TerrainData TerrainData;
        private Vector4 _terrainTextureScaleOffset;
        private Func<CanonicalTileId, float, float, float?> _canonicalElevationSampler;
        internal bool HasCanonicalElevationSampler => _canonicalElevationSampler != null;

        /// <summary>Optional shared source sampler used by CPU terrain and query paths.</summary>
        public void SetCanonicalElevationSampler(Func<CanonicalTileId, float, float, float?> sampler)
        {
            _canonicalElevationSampler = sampler;
        }

        /// <summary>
        /// Scale/offset Vector4 that maps this tile's UV [0,1] to the shared data tile's
        /// UV sub-region (data tile is <c>currentZ − 2</c> zoom coarser than this render
        /// tile). Exposed so the collider builder can lift sampling math out of its inner
        /// loop instead of calling <see cref="QueryHeightData(float,float)"/> per vertex.
        /// </summary>
        public Vector4 TerrainTextureScaleOffset => _terrainTextureScaleOffset;

        public UnityTileTerrainContainer(UnityMapTile unityMapTile, Action elevationUpdatedCallback, Action onDisposeCallback)
        {
            _unityMapTile = unityMapTile;
            _onDisposeCallback = onDisposeCallback;
            ElevationValuesUpdated = elevationUpdatedCallback;
        }

        /// <summary>
        /// Assigns terrain data to this tile: wires the material height texture, applies a
        /// conservative fallback mesh bounds (so shader-displaced verts don't get frustum-
        /// culled before real Min/MaxElevation arrive), and subscribes to future elevation
        /// updates.
        /// </summary>
        /// <param name="terrainData">Raster + optional decoded elevation for this tile.</param>
        /// <param name="useShaderElevation">When true, the material's <c>_ElevationMultiplier</c> is set to 1 so the vertex shader samples <c>_HeightTexture</c>; otherwise 0 (CPU-displaced verts).</param>
        /// <param name="state">Marks the tile container as temporary (using a parent's data while its own loads) or final.</param>
        public void SetTerrainData(TerrainData terrainData, bool useShaderElevation, TileContainerState state = TileContainerState.Final)
        {
            _canonicalElevationSampler = null;
            // Detach from the previous TerrainData unconditionally — null-callers
            // (deliberately or accidentally clearing the slot) would otherwise leak
            // the prior subscriptions on the shared TerrainData.
            if (TerrainData != null)
            {
                TerrainData.ElevationValuesUpdated -= OnElevationValuesUpdated;
                TerrainData.RemoveDisposeCallback(_onDisposeCallback);
            }

            if (terrainData == null)
            {
                TerrainData = null;
                return;
            }

            State = state;
            TerrainData = terrainData;
            // Add (don't replace) — multiple render tiles share one TerrainData.
            TerrainData.AddDisposeCallback(_onDisposeCallback);

            OnTerrainUpdated();

            // Apply a generous fallback bounds up front so shader-displaced geometry does not
            // get frustum-culled before real Min/MaxElevation arrive (or forever, if CPU
            // elevation extraction is disabled). OnElevationValuesUpdated later tightens the
            // bounds to the actual elevation range if the float[] is decoded.
            _unityMapTile.SetFallbackMeshBounds();

            // Subscribe multicast-safe so other listeners (e.g. ElevatedTerrainStrategy's
            // deferred collider rebuild) can coexist.
            TerrainData.ElevationValuesUpdated += OnElevationValuesUpdated;

            if (TerrainData.IsElevationDataReady)
            {
                OnElevationValuesUpdated();
            }

            _unityMapTile.PropertyBlock.SetFloat(ElevationMultiplier, useShaderElevation ? 1 : 0);
            _unityMapTile.ApplyPropertyBlock();
        }

        public void OnTerrainUpdated()
        {
            if (TerrainData == null)
                return;
        
            _terrainTextureScaleOffset = _unityMapTile.CanonicalTileId.CalculateScaleOffsetAtZoom(TerrainData.TileId.Z);

            var block = _unityMapTile.PropertyBlock;
            block.SetVector(HeightTextureST, _terrainTextureScaleOffset);
            block.SetTexture(HeightTexture, TerrainData.Texture);
            block.SetFloat("_IsFallbackTexture", 0);
            block.SetFloat(ElevationChangeTime, Time.time);
            _unityMapTile.ApplyPropertyBlock();
        }

        public void OnElevationValuesUpdated()
        {
            // Multicast dispose callback can fire on a container whose TerrainData was
            // already cleared (concurrent eviction during readback). Bail silently.
            if (TerrainData == null) return;
            TerrainData.IsElevationDataReady = true;
            ElevationValuesUpdated();
        }

        public TerrainData GetAndClearTerrainData()
        {
            _canonicalElevationSampler = null;
            if (TerrainData == null)
                return null;

            TerrainData.ElevationValuesUpdated -= OnElevationValuesUpdated;
            TerrainData.RemoveDisposeCallback(_onDisposeCallback);
            _unityMapTile.PropertyBlock.SetTexture(HeightTexture, Texture2D.grayTexture);
            _unityMapTile.ApplyPropertyBlock();
            var rd = TerrainData;
            TerrainData = null;
            return rd;
        }
        
        public float QueryHeightData(float x, float y)
        {
            var canonical = _canonicalElevationSampler?.Invoke(_unityMapTile.CanonicalTileId, x, y);
            if (canonical.HasValue) return canonical.Value;
            var values = TerrainData?.ElevationValues;
            if (values == null || values.Length == 0)
            {
                return 0;
            }

            var width = (int)Mathf.Sqrt(values.Length);
            var xx = TerrainRasterCoordinates.ToSampleCoordinate(x, _terrainTextureScaleOffset.x, _terrainTextureScaleOffset.z, width);
            var yy = TerrainRasterCoordinates.ToSampleCoordinate(y, _terrainTextureScaleOffset.y, _terrainTextureScaleOffset.w, width);
            return BilinearSampleElevation(values, width, xx, yy);
        }

        /// <summary>
        /// Bilinearly samples the decoded elevation array at (xx, yy) in data-texture
        /// pixel space. Render tiles share a coarser data tile with 16 neighbors, so a
        /// 129-vertex mesh routinely samples a ~64×64 data sub-region — multiple verts
        /// land inside the same texel. Nearest-neighbor sampling in that regime produces
        /// visible stepping; the four-corner lerp smooths it out for ~4 reads + 3 lerps
        /// per sample.
        /// </summary>
        private static float BilinearSampleElevation(float[] values, int width, float xx, float yy)
        {
            var x0 = (int)xx;
            var y0 = (int)yy;
            var x1 = x0 + 1; if (x1 >= width) x1 = width - 1;
            var y1 = y0 + 1; if (y1 >= width) y1 = width - 1;
            var fx = xx - x0;
            var fy = yy - y0;

            var row0 = y0 * width;
            var row1 = y1 * width;
            var h00 = values[row0 + x0];
            var h10 = values[row0 + x1];
            var h01 = values[row1 + x0];
            var h11 = values[row1 + x1];
            var h0 = h00 + (h10 - h00) * fx;
            var h1 = h01 + (h11 - h01) * fx;
            return h0 + (h1 - h0) * fy;
        }

        /// <summary>
        /// Hard-disable terrain for this tile: zeroes the shader elevation multiplier,
        /// unsubscribes from <see cref="TerrainData.ElevationValuesUpdated"/>, and drops the
        /// cached <see cref="TerrainData"/> reference. Call when the tile's zoom falls
        /// outside the supported range so nothing lingers on it.
        /// </summary>
        public void DisableTerrain()
        {
            State = TileContainerState.Final;
            GetAndClearTerrainData();
            _unityMapTile.PropertyBlock.SetFloat(ElevationMultiplier, 0);
            _unityMapTile.ApplyPropertyBlock();
        }

        public void OnDestroy()
        {
            if (TerrainData != null)
            {
                TerrainData.ElevationValuesUpdated -= OnElevationValuesUpdated;
                // Symmetric detach: previously only the ElevationValuesUpdated handler was
                // removed, but the multicast dispose callback stayed wired. On a later
                // eviction of shared TerrainData, that delegate would fire into a destroyed
                // tile — exactly the class of bug the multicast change was meant to fix.
                TerrainData.RemoveDisposeCallback(_onDisposeCallback);
                TerrainData = null;
            }
        }
    }
    
    public enum TileContainerState
    {
        Temporary,
        Final
    }
}
