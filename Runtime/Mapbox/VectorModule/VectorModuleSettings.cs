using System;
using Mapbox.BaseModule.Data.Interfaces;
using Mapbox.BaseModule.Map;
using UnityEngine;

namespace Mapbox.VectorModule
{
    [Serializable]
    public class VectorModuleSettings
    {
        public VectorSourceType SourceType;
        public string CustomSourceId;
        public VectorSourceSettings DataSettings;
        
        [Tooltip("Tile outside this range will be rejected.")]
        public Vector2 RejectTilesOutsideZoom = new Vector2(12, 16);

        [Min(0)]
        [Tooltip("Maximum number of generated vector-tile visuals retained inactive for fast reactivation. Zero preserves destructive unload behavior.")]
        public int WarmVisualTileCapacity = 0;
    }
}
