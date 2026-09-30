using System.Collections;
using System.Collections.Generic;
using Mapbox.BaseModule.Data;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Map;
using Mapbox.VectorModule.MeshGeneration;
using Mapbox.VectorTile;
using UnityEngine;

namespace Mapbox.VectorModule
{
    public interface IVectorLayerVisualizer
    {
        string VectorLayerName { get; }
        void AddModifierStack(List<ModifierStack> stack);
        Dictionary<int, HashSet<MeshData>> CreateMesh(CanonicalTileId tileId, VectorTileLayer layer);
        List<GameObject> CreateGo(CanonicalTileId tileId, Dictionary<int, HashSet<MeshData>> meshData);
        void UnregisterTile(CanonicalTileId tileId);
        bool Active { get; set; }
        IEnumerator Initialize();
        Dictionary<int, ModifierStack> GetModStacks { get; }
        void OnDestroy();
        void UpdateForView(CanonicalTileId canonicalTileId, IMapInformation information);
        void SetActive(CanonicalTileId tileId, bool isActive, IMapInformation mapInformation);
        bool ContainsVisualFor(CanonicalTileId dataTileId);
    }

    /// <summary>Explicit support for retaining generated visuals independently of source data.</summary>
    public interface IWarmVectorLayerVisualizer
    {
        void DeactivateWarm(CanonicalTileId tileId, IMapInformation mapInformation);
        void ReactivateWarm(CanonicalTileId tileId, IMapInformation mapInformation);
    }
}
