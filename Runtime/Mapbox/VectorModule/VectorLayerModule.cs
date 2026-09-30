using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Interfaces;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using Mapbox.VectorModule.MeshGeneration;
using Mapbox.VectorTile.ExtensionMethods;
using UnityEngine;
using Console = System.Console;

namespace Mapbox.VectorModule
{
	public class VectorLayerModule : ILayerModule
	{
		//mesh gen
		protected bool _isActive = true;
		protected UnityContext _unityContext;
		protected Dictionary<CanonicalTileId, List<TaskWrapper>> _activeTasks;
		protected Dictionary<string, List<IVectorLayerVisualizer>> _layerVisualizers;
		
		private Source<VectorData> _vectorSource;
		private VectorModuleSettings _vectorModuleSettings;
		private IMapInformation _mapInformation;
		
		//tiles we need to cover the ideal tile list
		private HashSet<CanonicalTileId> _retainedTiles;
		private HashSet<CanonicalTileId> _readyTiles;
		private List<CanonicalTileId> _tilesToRemove;
		private readonly LinkedList<CanonicalTileId> _warmTiles = new LinkedList<CanonicalTileId>();
		private readonly Dictionary<CanonicalTileId, LinkedListNode<CanonicalTileId>> _warmTileNodes = new Dictionary<CanonicalTileId, LinkedListNode<CanonicalTileId>>();
		private readonly Dictionary<CanonicalTileId, object> _generationTokens = new Dictionary<CanonicalTileId, object>();
		private readonly Dictionary<object, List<Action<MeshGenerationTaskResult>>> _generationWaiters = new Dictionary<object, List<Action<MeshGenerationTaskResult>>>();
		private bool _warmRetentionSupported;
		
		public VectorLayerModule(IMapInformation mapInformation, Source<VectorData> source, UnityContext unityContext, Dictionary<string, List<IVectorLayerVisualizer>> layerVisualizers, VectorModuleSettings vectorModuleSettings = null) : base()
		{
			_unityContext = unityContext;
			_layerVisualizers = layerVisualizers;
			_mapInformation = mapInformation;
			
			_vectorSource = source;
			if (_vectorSource != null)
			{
				_vectorSource.CacheItemDisposed += ClearDisposedDataVisual;
			}
			
			_vectorModuleSettings = vectorModuleSettings ?? new VectorModuleSettings();
			_warmRetentionSupported = _vectorModuleSettings.WarmVisualTileCapacity > 0 &&
				_layerVisualizers.Values.SelectMany(x => x).All(x => x is IWarmVectorLayerVisualizer);
			_readyTiles = new HashSet<CanonicalTileId>();
			_retainedTiles = new HashSet<CanonicalTileId>();
			_activeTasks = new Dictionary<CanonicalTileId, List<TaskWrapper>>();
			_tilesToRemove = new List<CanonicalTileId>(10);
		}

		public virtual IEnumerator Initialize()
		{
			if (_vectorSource != null)
			{
				yield return _vectorSource.Initialize();
			}
			foreach (var visualizers in _layerVisualizers.Values)
			{
				foreach (var visualizer in visualizers)
				{
					yield return visualizer.Initialize();
				}
			}
		}

		public virtual void LoadTempTile(UnityMapTile tile)
		{
			
		}

		public virtual bool LoadInstant(UnityMapTile unityTile)
		{
			var targetId = GetTargetTileId(unityTile.CanonicalTileId);
			if (_readyTiles.Contains(targetId))
			{
				ReactivateTile(targetId);
				return true;
			}
			else
			{
				//Debug.Log(string.Format("Load Instant {0}, {1}, {2}" ,unityTile.CanonicalTileId, _vectorSource.CheckInstantData(unityTile.CanonicalTileId), _visualCache.ContainsKey(unityTile.CanonicalTileId)));
				if (!IsZinSupportedRange(targetId.Z)) return true;

				//this is wrong, it feels wrong
				//tile doesn't need data, only yhe visual object. why are we checking for data
				if (_vectorSource.GetInstantData(targetId, out var instantData) &&
				    unityTile.TerrainContainer.State == TileContainerState.Final)
				{
					if (!IsMeshGenInWork(targetId))
					{
						CreateVisual(targetId, instantData);
					}
				}

				return false;
			}
		}

		public virtual bool RetainTiles(HashSet<CanonicalTileId> retainedTiles)
		{
			UpdateRetainedTiles(retainedTiles);

			_tilesToRemove.Clear();
			foreach (var tileId in _readyTiles.OrderBy(x => x.Z).ThenBy(x => x.X).ThenBy(x => x.Y).ToArray())
			{
				var isActive = _retainedTiles.Contains(tileId);
				if (isActive)
				{
					ReactivateTile(tileId);
				}
				else if (_warmRetentionSupported)
				{
					DeactivateTileWarm(tileId);
				}
				else
				{
					SetTileActive(tileId, false);
					_tilesToRemove.Add(tileId);
					if (_activeTasks.TryGetValue(tileId, out var tasks))
					{
						foreach (var task in tasks)
						{
							_activeTasks.Remove(tileId);
							task.Cancel();
						}
					}
				}
			}

			foreach (var tileId in _tilesToRemove)
			{
				ClearDisposedDataVisual(tileId);
			}
			EvictWarmTilesToCapacity();

			//cancel tasks for tiles we no longer need
			//this prevents flickers from buildings appearing in temp tiles
			//while we are waiting for the final real tile
			foreach (var taskPair in _activeTasks)
			{
				if (!_retainedTiles.Contains(taskPair.Key))
				{
					foreach (var task in taskPair.Value)
					{
						task.Cancel();
					}
				}
			}

			var isReady = _vectorSource.RetainTiles(_retainedTiles);
			return isReady;
		}

		public virtual void UpdatePositioning(IMapInformation information)
		{
			foreach (var tileId in _readyTiles)
			{
				var isRetained = _retainedTiles.Contains(tileId);
				if (isRetained)
				{
					UpdateForView(tileId, _mapInformation);
				}
			}
		}
		
		public virtual void OnDestroy()
		{
			if (!_isActive)
			{
				return;
			}
			_isActive = false;
			if (_vectorSource != null)
			{
				_vectorSource.CacheItemDisposed -= ClearDisposedDataVisual;
			}
			foreach (var tasks in _activeTasks.Values)
			{
				foreach (var task in tasks)
				{
					task.Cancel();
				}
			}
			_activeTasks.Clear();
			_warmTiles.Clear();
			_warmTileNodes.Clear();
			CompleteAllGenerationWaiters(new MeshGenerationTaskResult(TaskResultType.Cancelled));
			_generationTokens.Clear();
			foreach (var pair in _layerVisualizers)
			{
				foreach (var visualizer in pair.Value)
				{
					visualizer.OnDestroy();
				}
			}
		}

		public void ReloadTile(CanonicalTileId tile)
		{
			var targetId = GetTargetTileId(tile);
			if (_readyTiles.Contains(targetId) && _vectorSource.GetInstantData(targetId, out var instantData))
			{
				ClearDisposedDataVisual(targetId);
				CreateVisual(targetId, instantData);
			}
		}

		public IEnumerable<CanonicalTileId> GetReadyTiles()
		{
			// Ready means generated visuals; warm visuals remain ready while outside the active cover.
			return _readyTiles;
		}

		public bool TryGetLayerVisualizer(string name, out IVectorLayerVisualizer visualizer)
		{
			if (_layerVisualizers.TryGetValue(name, out var visualizers))
			{
				visualizer = visualizers.FirstOrDefault();
				return true;
			}

			visualizer = null;
			return false;
		}
		
		public IEnumerable<CanonicalTileId> GetDataId(IEnumerable<CanonicalTileId> tileIdList)
		{
			return tileIdList.Select(GetTargetTileId).Distinct();
		}
		
		//COROUTINE METHODS only used in initialization so far
		#region coroutines
		public IEnumerator LoadAndProcessTileCoroutine(CanonicalTileId tile)
		{
			VectorData tileData = null;
			yield return _vectorSource.LoadTileCoroutine(tile, data => tileData = data);
			if (tileData != null)
			{
				yield return CreateVisualCoroutine(tile, tileData);
			}
		}
		
		public virtual IEnumerator LoadTileData(CanonicalTileId tileId, Action<MapboxTileData> callback = null)
		{
			yield return _vectorSource.LoadTileCoroutine(tileId, callback);
		}

		public virtual IEnumerator ProcessTileData(CanonicalTileId tileId)
		{
			if (_vectorSource.GetInstantData(tileId, out var data))
			{
				yield return CreateVisualCoroutine(tileId, data);
			}
		}

		public virtual IEnumerator LoadTiles(IEnumerable<CanonicalTileId> tiles)
		{
			//this section loaded all data first and started processing once they are all loaded
			//commented this out and replaced it with the section below
			//new version loads and processes the data per tile so process doesn't wait for all tiles to load
			//performance difference is almost non-existent, second version felt more correct
			//---
			// List<VectorData> loadedTiles = null;
			// yield return _vectorSource.LoadTilesCoroutine(GetTargetTileId(tiles), (result) => { loadedTiles = result; });
			// var visualGenerations = loadedTiles.Select(x => CreateVisualCoroutine(x.TileId, x));
			// yield return visualGenerations.WaitForAll();
			//---

			var targetTileIds = new HashSet<CanonicalTileId>();
			foreach (var tile in tiles)
			{
				var targetId = GetTargetTileId(tile);
				if (IsZinSupportedRange(targetId.Z))
				{
					targetTileIds.Add(targetId);
				}
			}

			//we calculate the targetTileIds first and then start the process because multiple tiles targeting same
			//vector parent tile will cause problems inside the LoadAndProcessTileCoroutine method
			yield return targetTileIds.Select(LoadAndProcessTileCoroutine).WaitForAll();
		}

		public IEnumerable<IEnumerator> GetTileCoverCoroutines(IEnumerable<CanonicalTileId> tiles)
		{
			var targetTiles = tiles.Where(x => IsZinSupportedRange(x.Z)).Select(GetTargetTileId).Distinct();
			return targetTiles.Select(LoadAndProcessTileCoroutine);
		}
		
		#endregion
		
		
		
		
		private bool IsZinSupportedRange(int targetZ)
		{
			return _vectorModuleSettings.RejectTilesOutsideZoom.x <= targetZ && _vectorModuleSettings.RejectTilesOutsideZoom.y >= targetZ;
		}
		
		private void UpdateRetainedTiles(HashSet<CanonicalTileId> retainedTiles)
		{
			_retainedTiles.Clear();
			foreach (var tileId in retainedTiles)
			{
				var targetId = GetTargetTileId(tileId);
				if (IsZinSupportedRange(targetId.Z))
				{
					if(targetId.Z < _vectorModuleSettings.RejectTilesOutsideZoom.x)
						continue;
					_retainedTiles.Add(targetId);
				}
				
				//this helps when it tries to load new higher level tiles when it isn't loaded yet
				//but we want to keep its existing around and not get recycled until children loads
				if (!_readyTiles.Contains(targetId))
				{
					//checking direct children for smoother zoom out
					//this replaces the old activeTiles list and logic
					//if you remove this, you'll see building continuity broken in example switching from z15 to z14
					for (int i = 0; i < 4; i++)
					{
						var child = targetId.Quadrant(i);
						if (_readyTiles.Contains(child))
						{
							_retainedTiles.Add(child);
						}
					}
					
					for (int i = targetId.Z; i >= _vectorModuleSettings.RejectTilesOutsideZoom.x; i--)
					{
						targetId.MoveToParent();
						if (_readyTiles.Contains(targetId))
						{
							_retainedTiles.Add(targetId);
							break;
						}
					}
				}
			}
		}
		
		private CanonicalTileId GetTargetTileId(CanonicalTileId tileId)
		{
			var maxZoom = _vectorModuleSettings.DataSettings.ClampDataLevelToMax;
			if (tileId.Z >= maxZoom)
			{
				return tileId.Z > maxZoom
					? tileId.ParentAt(maxZoom)
					: tileId;
			}
			else
			{
				return tileId;
			}
		}
		
		private IEnumerable<CanonicalTileId> GetTargetTileId(IEnumerable<CanonicalTileId> tileIdList)
		{
			return tileIdList.Select(GetTargetTileId).ToList();
		}
		
		private void CreateVisual(CanonicalTileId tileId, VectorData vectorData, Action<MeshGenerationTaskResult> callback = null)
		{
			if (_readyTiles.Contains(tileId))
			{
				callback?.Invoke(new MeshGenerationTaskResult(TaskResultType.Success));
			}
			else if (!_generationTokens.ContainsKey(tileId) && !IsMeshGenInWork(vectorData.TileId))
			{
				var generationToken = new object();
				_generationTokens[tileId] = generationToken;
				MeshGeneration(vectorData, (result =>
				{
					var isCurrentGeneration = _generationTokens.TryGetValue(tileId, out var currentToken) &&
						ReferenceEquals(generationToken, currentToken);
					if (isCurrentGeneration)
					{
						_generationTokens.Remove(tileId);
					}
					if (!isCurrentGeneration || !_isActive)
					{
						DestroyGeneratedObjects(result);
						callback?.Invoke(new MeshGenerationTaskResult(TaskResultType.Cancelled));
						return;
					}
					if (result != null)
					{
						if (result.ResultType == TaskResultType.Cancelled)
						{
							result.InvalidateAndRetry = _retainedTiles.Contains(tileId);
						}
						switch (result.ResultType)
						{
							case TaskResultType.Success:
								_readyTiles.Add(tileId);
								OnVectorMeshCreated(tileId, result.GeneratedObjects);
								UpdateForView(tileId, _mapInformation);
								break;
							case TaskResultType.DataProcessingFailure:
							case TaskResultType.MeshGenerationFailure:
								_vectorSource.InvalidateData(vectorData.TileId);
								Debug.Log(result.ExceptionsAsString);
								break;
							case TaskResultType.Cancelled:
							{
								if (result != null && result.GeneratedObjects != null)
								{
									foreach (var gameObject in result?.GeneratedObjects)
									{
										GameObject.Destroy(gameObject);
									}
								}

								break;
							}
						}
					}

					callback?.Invoke(result);;
					CompleteGenerationWaiters(generationToken, result);
				}));
			}
			else if (callback != null && _generationTokens.TryGetValue(tileId, out var generationToken))
			{
				if (!_generationWaiters.TryGetValue(generationToken, out var waiters))
				{
					waiters = new List<Action<MeshGenerationTaskResult>>();
					_generationWaiters.Add(generationToken, waiters);
				}
				waiters.Add(callback);
			}
		}

		private void CompleteGenerationWaiters(object generationToken, MeshGenerationTaskResult result)
		{
			if (!_generationWaiters.TryGetValue(generationToken, out var waiters))
			{
				return;
			}
			_generationWaiters.Remove(generationToken);
			foreach (var waiter in waiters)
			{
				waiter(result);
			}
		}

		private void CompleteAllGenerationWaiters(MeshGenerationTaskResult result)
		{
			var tokens = _generationWaiters.Keys.ToArray();
			foreach (var token in tokens)
			{
				CompleteGenerationWaiters(token, result);
			}
		}

		private static void DestroyGeneratedObjects(MeshGenerationTaskResult result)
		{
			if (result?.GeneratedObjects == null)
			{
				return;
			}
			foreach (var gameObject in result.GeneratedObjects)
			{
				if (gameObject != null)
				{
					gameObject.SetActive(false);
					GameObject.Destroy(gameObject);
				}
			}
		}

		private IEnumerator CreateVisualCoroutine(CanonicalTileId tileId, VectorData vectorData, Action<MeshGenerationTaskResult> callback = null)
		{
			while (true)
			{
				var isMeshGenDone = false;
				MeshGenerationTaskResult generationResult = null;
				CreateVisual(tileId, vectorData, result =>
				{
					generationResult = result;
					isMeshGenDone = true;
				});
				while (!isMeshGenDone)
				{
					yield return null;
				}
				if (generationResult != null && generationResult.InvalidateAndRetry && _isActive)
				{
					yield return null;
					continue;
				}
				callback?.Invoke(generationResult);
				yield break;
			}
		}

		private void ClearDisposedDataVisual(CanonicalTileId tileId)
		{
			// Source bytes can be discarded while a visual is warm: warm-capable
			// implementations promise their generated state is independent of those bytes.
			if (_warmRetentionSupported && _warmTileNodes.ContainsKey(tileId))
			{
				return;
			}
			if (_generationTokens.TryGetValue(tileId, out var generationToken))
			{
				_generationTokens.Remove(tileId);
				CompleteGenerationWaiters(generationToken, new MeshGenerationTaskResult(TaskResultType.Cancelled));
			}
			RemoveWarmTile(tileId);
			var hadReadyVisual = _readyTiles.Remove(tileId);
			var hadTasks = _activeTasks.TryGetValue(tileId, out var tasks);
			if (!hadReadyVisual && !hadTasks)
			{
				return;
			}
			if (hadTasks)
			{
				foreach (var task in tasks)
				{
					task.Cancel();
				}
				_activeTasks.Remove(tileId);
			}
			foreach (var pair in _layerVisualizers)
			{
				foreach (var visualizer in pair.Value)
				{
					visualizer.UnregisterTile(tileId);
				}
			}

			OnVectorMeshDestroyed(tileId);
		}

		protected bool IsCurrentTask(CanonicalTileId tileId, TaskWrapper task)
		{
			return _activeTasks.TryGetValue(tileId, out var tasks) && tasks.Contains(task);
		}

		protected void RemoveCurrentTask(CanonicalTileId tileId, TaskWrapper task)
		{
			if (!_activeTasks.TryGetValue(tileId, out var tasks))
			{
				return;
			}
			tasks.Remove(task);
			if (tasks.Count == 0)
			{
				_activeTasks.Remove(tileId);
			}
		}

		private void SetTileActive(CanonicalTileId tileId, bool isActive)
		{
			foreach (var visualizer in _layerVisualizers.Values.SelectMany(x => x))
			{
				visualizer.SetActive(tileId, isActive, _mapInformation);
			}
		}

		private void DeactivateTileWarm(CanonicalTileId tileId)
		{
			// Repeated cover updates must not make an old entry appear recently used.
			if (_warmTileNodes.ContainsKey(tileId))
			{
				return;
			}
			foreach (var visualizer in _layerVisualizers.Values.SelectMany(x => x))
			{
				((IWarmVectorLayerVisualizer)visualizer).DeactivateWarm(tileId, _mapInformation);
			}
			_warmTileNodes.Add(tileId, _warmTiles.AddLast(tileId));
		}

		private void ReactivateTile(CanonicalTileId tileId)
		{
			if (_warmRetentionSupported && _warmTileNodes.ContainsKey(tileId))
			{
				// Refresh the transform against current map state before any object is visible.
				UpdateForView(tileId, _mapInformation);
				foreach (var visualizer in _layerVisualizers.Values.SelectMany(x => x))
				{
					((IWarmVectorLayerVisualizer)visualizer).ReactivateWarm(tileId, _mapInformation);
				}
				RemoveWarmTile(tileId);
				return;
			}
			SetTileActive(tileId, true);
		}

		private void RemoveWarmTile(CanonicalTileId tileId)
		{
			if (_warmTileNodes.TryGetValue(tileId, out var node))
			{
				_warmTiles.Remove(node);
				_warmTileNodes.Remove(tileId);
			}
		}

		private void EvictWarmTilesToCapacity()
		{
			var capacity = Math.Max(0, _vectorModuleSettings.WarmVisualTileCapacity);
			while (_warmTiles.Count > capacity)
			{
				var oldest = _warmTiles.First.Value;
				RemoveWarmTile(oldest);
				ClearDisposedDataVisual(oldest);
			}
		}
		
		private bool IsMeshGenInWork(CanonicalTileId tileId) { return _activeTasks.ContainsKey(tileId); }
		
		private void UpdateForView(CanonicalTileId tileId, IMapInformation information)
		{
			foreach (var pair in _layerVisualizers)
			{
				foreach (var visualizer in pair.Value)
				{
					visualizer.UpdateForView(tileId, information);
				}
			}
		}
		
		protected virtual void MeshGeneration(VectorData data, Action<MeshGenerationTaskResult> callback)
        {
            if (data.Data == null)
            {
                callback(new MeshGenerationTaskResult(TaskResultType.Success));
                return;
            }

            MeshGenTaskWrapper<MeshGenTaskWrapperResult> meshTask = null;
            meshTask = new MeshGenTaskWrapper<MeshGenTaskWrapperResult>()
            {
                TileId = data.TileId,
                DataAction = () =>
                {
                    var result = new MeshGenTaskWrapperResult();
                    try
                    {
                        var decompressed = Compression.Decompress(data.Data);
                        data.VectorTileData = new Mapbox.VectorTile.VectorTile(decompressed);
                    }
                    catch (Exception e)
                    {
                        result.ResultType = TaskResultType.DataProcessingFailure;
                        result.AddException(e);
                        return result;
                    }

                    try
                    {
                        var layers = data.VectorTileData.LayerNames();
                        foreach (var layerName in layers)
                        {
                            if (_layerVisualizers.TryGetValue(layerName, out var layerVisualizers))
                            {
	                            for (var vi = 0; vi < layerVisualizers.Count; vi++)
	                            {
		                            var layerVisualizer = layerVisualizers[vi];
		                            if (layerVisualizer.ContainsVisualFor(data.TileId))
			                            continue;
		                            if (layerVisualizer.Active)
		                            {
			                            var key = layerVisualizers.Count > 1 ? $"{layerName}_{vi}" : layerName;
			                            result.Data.Add(key, layerVisualizer.CreateMesh(data.TileId, data.VectorTileData.GetLayer(layerName)));
		                            }
	                            }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        result.ResultType = TaskResultType.MeshGenerationFailure;
                        result.AddException(e);
                        return result;
                    }

                    result.ResultType = TaskResultType.Success;
                    return result;
                },
                DataCompleted = (task, taskResult) => //task may be null
                {
                    if (!_isActive)
                    {
                        callback(new MeshGenerationTaskResult(TaskResultType.Cancelled));
                        return;
                    }

                    if (!IsCurrentTask(data.TileId, meshTask))
                    {
                        callback(new MeshGenerationTaskResult(TaskResultType.Cancelled));
                        return;
                    }
                    RemoveCurrentTask(data.TileId, meshTask);
					
                    if (taskResult.ResultType == TaskResultType.Cancelled || meshTask.IsCancelled || (task != null && task.IsCanceled))
                    {
	                    var failResult = new MeshGenerationTaskResult(TaskResultType.Cancelled);
	                    callback(failResult);
	                    return;
                    }
                    
                    if (taskResult.ResultType == TaskResultType.MeshGenerationFailure)
                    {
                        var failResult = new MeshGenerationTaskResult(taskResult.ResultType);
                        foreach (var e in taskResult.GetExceptions())
                        {
                            failResult.AddException(e);
                        }
                        //Debug.Log(string.Format("{0} mesh gen exception: {1}", data.TileId, task.Exception.Message));
                        failResult.AddException(new Exception(string.Format("{0} mesh gen exception: {1}", data.TileId, taskResult.ExceptionsAsString)));
                        callback(failResult);
                        return;
                    }
                    
					
                    var resultGameObjects = new List<GameObject>();
                    foreach (var layerName in data.VectorTileData.LayerNames())
                    {
                        if (_layerVisualizers.TryGetValue(layerName, out var layerVisualizers))
                        {
	                        for (var vi = 0; vi < layerVisualizers.Count; vi++)
	                        {
		                        var key = layerVisualizers.Count > 1 ? $"{layerName}_{vi}" : layerName;
		                        if (!taskResult.Data.TryGetValue(key, out var tileMeshData))
			                        continue;
		                        var layerVisualizer = layerVisualizers[vi];
		                        var layerGameObjects = layerVisualizer.CreateGo(data.TileId, tileMeshData);
		                        foreach (var gameObject in layerGameObjects)
		                        {
			                        gameObject.SetActive(false);
			                        resultGameObjects.Add(gameObject);
		                        }
	                        }
                        }
                    }
                    callback(new MeshGenerationTaskResult(TaskResultType.Success, resultGameObjects));
                    
                }
            };

            if(!_activeTasks.ContainsKey(data.TileId)) _activeTasks.Add(data.TileId, new List<TaskWrapper>());
            _activeTasks[data.TileId].Add(meshTask);
            _unityContext.TaskManager.AddTask(meshTask, 0);
        }
		
		public Action<CanonicalTileId, IEnumerable<GameObject>> OnVectorMeshCreated = (tileId, gameobjects) => {};
		public Action<CanonicalTileId> OnVectorMeshDestroyed = (tileId) => {};
	}
}
