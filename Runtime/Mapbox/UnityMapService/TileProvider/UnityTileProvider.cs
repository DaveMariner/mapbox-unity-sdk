using System;
using System.Collections.Generic;
using Mapbox.BaseModule.Data.Interfaces;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using UnityEngine;

namespace Mapbox.UnityMapService.TileProviders
{
	[Serializable]
	public class UnityTileProviderSettings : ISerializationCallbackReceiver
	{
		/// <summary>
		/// Camera used for frustum culling and LOD calculations.
		/// Falls back to Camera.main if not set.
		/// </summary>
		[Tooltip("Camera used for frustum culling and LOD calculations. Falls back to Camera.main if not set.")]
		public Camera Camera;

		/// <summary>
		/// Tiles below this zoom level are always subdivided without distance checks.
		/// Higher values force more subdivisions, producing more tiles to fill the view.
		/// Lower values improve performance. Default of 2 works well for most cases.
		/// </summary>
		[Tooltip("Tiles below this zoom are always subdivided regardless of distance. Lower values improve performance. Default 2 works for most cases.")]
		public float MinimumZoomLevel = 2;

		/// <summary>
		/// Maximum zoom level the quadtree will subdivide to, clamped by the map's AbsoluteZoom.
		/// Higher values produce finer detail near the camera but cost more to render.
		/// Lower values improve performance at the expense of close-up detail.
		/// Typical range is 14-18 for most applications.
		/// </summary>
		[Tooltip("Maximum tile detail level. Higher = sharper close-up detail but more tiles to render. Typical range 14-18. Default 22.")]
		public float MaximumZoomLevel = 22;

		/// <summary>
		/// Controls how aggressively tiles subdivide with distance.
		/// Higher values produce more tiles with finer detail at distance but cost more to render.
		/// Lower values improve performance with coarser detail at distance.
		/// Values at or below 0 collapse the split-distance to zero and render only the
		/// MinimumZoomLevel tile — kept guarded in OnAfterDeserialize for legacy scenes
		/// saved before this field existed (Unity deserializes missing fields as default(T)
		/// and does NOT run the C# field initializer in that case).
		/// </summary>
		[Tooltip("Tile detail vs. performance. Higher = more detail at distance, lower = better performance. Below 0.3 may reduce quality. Default 0.6.")]
		public float SubdivisionBias = 0.6f;

		/// <summary>
		/// Expands the 4 side frustum planes outward by this distance, in Unity world units.
		/// Pre-loads tiles just outside the screen edges to prevent pop-in during camera pans.
		/// Higher values give smoother panning but load more off-screen tiles.
		/// Does not affect near/far planes.
		/// Tile size varies with <c>mapInformation.Scale</c>: a tile is ~1 unit at scale 1,
		/// ~100 units at scale 0.01. Pick a value relative to your scene's current tile size
		/// (e.g. ~25% of a tile width for a one-tile-wide buffer). Set to 0 to disable.
		/// </summary>
		[Tooltip("Pre-loads tiles beyond screen edges to reduce pop-in during panning. Value is in Unity world units — scale relative to the tile size at your map's current Scale. Set to 0 to disable.")]
		public float FrustumBuffer = 0f;

		public UnityTileProviderSettings(Camera cam, float minZoom = 2, float maxZoom = 22)
		{
			Camera = cam;
			MinimumZoomLevel = minZoom;
			MaximumZoomLevel = maxZoom;
		}

		public void OnBeforeSerialize() { }

		public void OnAfterDeserialize()
		{
			// Migrate legacy scenes saved before SubdivisionBias existed (Unity assigns
			// default(float) = 0 in that case, collapsing distToSplit and forcing only
			// the MinimumZoomLevel tile to render).
			if (SubdivisionBias <= 0f) SubdivisionBias = 0.6f;
		}
	}

	public class UnityTileProvider : TileProvider
	{
		public UnityTileProviderSettings Settings;
		protected int _maxZoom;
		protected readonly Plane[] _planes = new Plane[6];
		protected TileNode[] _pool;
		protected int _poolCount;
		protected readonly Stack<int> _stack;

		// Reusable array for 4 bottom-plane corners to avoid per-frame allocation
		protected readonly Vector3[] _corners = new Vector3[4];

		public UnityTileProvider(UnityTileProviderSettings settings)
		{
			Settings = settings;

			if (Settings.Camera == null) Settings.Camera = Camera.main;

			if (Settings.MinimumZoomLevel > Settings.MaximumZoomLevel)
			{
				Debug.LogWarning($"UnityTileProvider: MinimumZoomLevel ({Settings.MinimumZoomLevel}) is greater than MaximumZoomLevel ({Settings.MaximumZoomLevel}). Tiles will be force-split until the absolute zoom cap. Check the Inspector configuration.");
			}

			_pool = new TileNode[256];
			_stack = new Stack<int>(256);
		}

		protected int AllocNode()
		{
			if (_poolCount >= _pool.Length)
			{
				var newPool = new TileNode[_pool.Length * 2];
				Array.Copy(_pool, newPool, _pool.Length);
				_pool = newPool;
			}

			return _poolCount++;
		}

		public override bool GetTileCover(IMapInformation mapInformation, TileCover tileCover)
		{
			_poolCount = 0;
			_stack.Clear();
			tileCover.Tiles.Clear();

			// Cap at 30: distToSplit uses (1 << (_maxZoom - zoom)), which overflows int
			// past 30. Practically unreachable in Mercator (pyramid maxes around z22-24)
			// but guards against Inspector misconfig of MaximumZoomLevel.
			_maxZoom = (int)Mathf.Min(30f, Mathf.Min(Settings.MaximumZoomLevel, mapInformation.AbsoluteZoom));
			var cam = Settings.Camera;
			GeometryUtility.CalculateFrustumPlanes(cam, _planes);

			var camPos = cam.transform.position;
			var camForward = cam.transform.forward;
			var maxDistSq = cam.farClipPlane * cam.farClipPlane;

			// Expand the 4 side frustum planes (indices 0-3) outward for buffer tile pre-loading.
			// Near (4) and far (5) planes are left unchanged.
			if (Settings.FrustumBuffer > 0f)
			{
				for (int i = 0; i < 4; i++)
					_planes[i].distance += Settings.FrustumBuffer;
			}
			var worldCenter = mapInformation.CenterMercator;
			var scale = mapInformation.Scale;

			// Bounds extent for frustum culling: derived from observed terrain elevation.
			// Min/Max are in meters; divide by scale to get world units. Bottom and top
			// follow the observed range directly — clamping the bottom to 0 pessimized
			// culling for elevated-only terrain (Himalayan/Andean tiles with Min ≥ +500m)
			// by doubling the AABB volume below the actual surface. Negative Min (Death
			// Valley, Dead Sea) is handled naturally by letting the bottom go negative.
			// Floor on the total height ensures tiles are never zero-height (invisible).
			var maxWorld = mapInformation.Terrain.MaxElevation / scale;
			var minWorld = mapInformation.Terrain.MinElevation / scale;
			var boundsBottom = minWorld;
			var boundsHeight = Mathf.Max(maxWorld - boundsBottom, 0.1f);

			// Camera height above the map plane (y=0)
			var cameraHeight = camPos.y;

			// zoomSplitDistance: the world-space distance at which a single maxZoom tile
			// subtends half the screen height. Derived from the screen-fraction threshold:
			//   tileSize / dist / (2 * tan(fov/2)) > 0.5  →  dist < tileSize / tan(fov/2)
			// For a tile at zoom z: distToSplit = (1 << (maxZoom - z)) * zoomSplitDistance
			var halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
			var maxZoomTileSize = Conversions.TileSizeInUnitySpace(_maxZoom, scale);
			var zoomSplitDistance = maxZoomTileSize / halfFovTan;

			// push root tile
			var rootIdx = AllocNode();
			_pool[rootIdx].Set(new UnwrappedTileId(0, 0, 0), worldCenter, scale, boundsBottom, boundsHeight);
			_stack.Push(rootIdx);

			while (_stack.Count > 0)
			{
				var idx = _stack.Pop();
				ref var node = ref _pool[idx];

				if (node.Bounds.SqrDistance(camPos) > maxDistSq)
					continue;

				if (!GeometryUtility.TestPlanesAABB(_planes, node.Bounds))
					continue;

				var zoom = node.Id.Z;

				if (zoom >= _maxZoom || !ShouldSplit(zoom, node.Bounds, camPos, camForward, cameraHeight, zoomSplitDistance))
				{
					tileCover.Tiles.Add(node.Id);
					continue;
				}

				// subdivide into 4 children
				for (int i = 0; i < 4; i++)
				{
					var childX = (node.Id.X << 1) + (i & 1);
					var childY = (node.Id.Y << 1) + (i >> 1);
					var childIdx = AllocNode();
					_pool[childIdx].Set(
						new UnwrappedTileId(zoom + 1, childX, childY),
						worldCenter, scale, boundsBottom, boundsHeight);
					_stack.Push(childIdx);
				}
			}

			var mapCenter = mapInformation.LatitudeLongitude;
			var coverContainsMapCenter = false;
			foreach (var tileId in tileCover.Tiles)
			{
				var expectedTileAtZoom = Conversions.LatitudeLongitudeToTileId(mapCenter, tileId.Z).Canonical;
				if (tileId.Canonical.X == expectedTileAtZoom.X && tileId.Canonical.Y == expectedTileAtZoom.Y)
				{
					coverContainsMapCenter = true;
					break;
				}
			}

			if (!coverContainsMapCenter)
			{
				var centerMercator = mapInformation.CenterMercator;
				var centerMercatorRoundTrip = Conversions.WebMercatorToLatLon(centerMercator);
				var centerTile = Conversions.LatitudeLongitudeToTileId(mapCenter, _maxZoom);
				var coverDescription = new List<string>(tileCover.Tiles.Count);
				foreach (var tileId in tileCover.Tiles)
				{
					var canonicalTileId = tileId.Canonical;
					var tileBounds = Conversions.TileIdToBounds(canonicalTileId);
					coverDescription.Add($"{tileId}->canonical:{canonicalTileId},lat:{tileBounds.South:F5}..{tileBounds.North:F5},lon:{tileBounds.West:F5}..{tileBounds.East:F5}");
				}

				// var cameraParent = cam.transform.parent;
				// var cameraRoot = cam.transform.root;
				// var parentDescription = cameraParent == null
				// 	? "<none>"
				// 	: $"{cameraParent.name},worldPos:{cameraParent.position},worldRot:{cameraParent.rotation.eulerAngles},lossyScale:{cameraParent.lossyScale}";
				// // Debug.Log($"[TILE-DIAG][provider] map center={mapCenter.Latitude:F6},{mapCenter.Longitude:F6} mercatorXY=({centerMercator.x:F2},{centerMercator.y:F2}) roundTrip={centerMercatorRoundTrip.Latitude:F6},{centerMercatorRoundTrip.Longitude:F6} scale={scale} maxZoom={_maxZoom} expectedCenterXYZ={centerTile} coverCount={tileCover.Tiles.Count} coverContainsCenter=false camera={cam.name} cameraWorldPos={camPos} cameraLocalPos={cam.transform.localPosition} cameraForward={camForward} cameraEuler={cam.transform.eulerAngles} fov={cam.fieldOfView} far={cam.farClipPlane} pixelSize={cam.pixelWidth}x{cam.pixelHeight} parent={parentDescription} root={cameraRoot.name}@{cameraRoot.position} rootRot={cameraRoot.rotation.eulerAngles} cover=[{string.Join(" | ", coverDescription)}]");
			}

			return true;
		}

		// Cap for the projDist<=0 "always split" early-exit. Without a cap, a single
		// behind-camera corner (admissible after FrustumBuffer expansion) forces 4-way
		// subdivision recursively up to _maxZoom — pathologically expensive on tilted
		// views where many tiles are partially behind the near plane. 18 is generous
		// enough for any reasonable detail level.
		private const int AlwaysSplitSafeCap = 18;

		protected bool ShouldSplit(int zoom, Bounds bounds, Vector3 camPos,
			Vector3 camForward, float cameraHeight, float zoomSplitDistance)
		{
			if (zoom < Settings.MinimumZoomLevel)
				return true;

			// Find closest forward-projected distance across the 4 bottom-plane corners.
			// On a flat map, the closest projected corner is always on the bottom plane.
			var min = bounds.min;
			var max = bounds.max;
			var bottomY = min.y;
			_corners[0] = new Vector3(min.x, bottomY, min.z);
			_corners[1] = new Vector3(max.x, bottomY, min.z);
			_corners[2] = new Vector3(min.x, bottomY, max.z);
			_corners[3] = new Vector3(max.x, bottomY, max.z);

			var closestDist = float.MaxValue;
			for (int i = 0; i < 4; i++)
			{
				var offset = _corners[i] - camPos;
				// Project onto the ground plane: native SDK overrides this to +cameraHeight,
				// which in its tile-coord system stays geometrically consistent. Unity world
				// units flip the sign of the vertical dot-product contribution and trigger the
				// projDist<=0 early-exit (always-split) for downward-pitched cameras, so we
				// zero out the vertical and use ground-plane forward distance instead.
				offset.y = 0f;
				var projDist = Vector3.Dot(offset, camForward);
				if (projDist <= 0f)
				{
					// Corner behind camera plane — split for better fidelity on the
					// partly-visible portion, but cap so we don't recurse all the way
					// to _maxZoom (the FrustumBuffer expansion admits behind-camera
					// tiles, which would otherwise feed an endless split loop).
					return zoom < AlwaysSplitSafeCap;
				}
				if (projDist < closestDist)
					closestDist = projDist;
			}

			// Exponential distance threshold: doubles for each zoom level below maxZoom
			var distToSplit = (1 << (_maxZoom - zoom)) * zoomSplitDistance * Settings.SubdivisionBias;

			// Acute angle compensation: reduce detail for tiles viewed at grazing angles
			distToSplit *= DistToSplitScale(cameraHeight, closestDist);

			return closestDist < distToSplit;
		}

		/// <summary>
		/// Scales the split distance for tiles viewed at acute angles (high pitch / near horizon).
		/// When the angle between the camera-to-tile ray and the tile plane drops below 45 degrees,
		/// progressively increases the split distance using a geometric series, making it harder
		/// for distant foreshortened tiles to subdivide.
		/// Ported from native SDK tile_cover.cpp distToSplitScale (lines 263-285).
		/// </summary>
		private static float DistToSplitScale(float dz, float d)
		{
			const float kAcuteAngleThresholdSin = 0.707f; // sin(45 degrees)
			const float kStretchTile = 1.1f;
			const float DzFloor = 0.0001f;

			// Camera at (or extremely close to) ground level. Clamping dz to a tiny
			// epsilon and then computing r = d/dz makes Pow(1.1, k+1) saturate to Inf
			// and the function return ~0, which collapses distToSplit to 0 — the tile
			// pins to MinimumZoomLevel instead of subdividing. Street-level walking-sim
			// cameras hit this. Return 1.0 (no acute-angle compensation) so the caller's
			// normal split distance applies.
			if (dz < DzFloor)
				return 1f;

			if (d * kAcuteAngleThresholdSin < dz)
				return 1f;

			var r = d / dz;
			var k = r - 1f / kAcuteAngleThresholdSin;
			return r / (1f / kAcuteAngleThresholdSin +
				(Mathf.Pow(kStretchTile, k + 1f) - 1f) / (kStretchTile - 1f) - 1f);
		}

		protected struct TileNode
		{
			public UnwrappedTileId Id;
			public Bounds Bounds;

			public void Set(UnwrappedTileId id, Vector2d worldCenter, float scale, float boundsBottom, float boundsHeight)
			{
				Id = id;
				var rect = Conversions.TileBoundsInUnitySpace(id, worldCenter, scale);
				var sizeX = (float)rect.Size.x;

				// AABB extends from Y=boundsBottom up to boundsBottom+boundsHeight, so the
				// center sits halfway between. Lets Death-Valley-like tiles whose surface
				// is below Y=0 still pass the frustum test.
				var centerY = boundsBottom + boundsHeight * 0.5f;
				Bounds = new Bounds(
					new Vector3((float)rect.Center.x, centerY, (float)rect.Center.y),
					new Vector3(sizeX, boundsHeight, Mathf.Abs(sizeX)));
			}
		}
	}
}
