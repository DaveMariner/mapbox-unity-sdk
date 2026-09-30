using System;
using System.Collections.Generic;
using System.Linq;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using Mapbox.VectorModule.ComponentSystem.Data;
using Mapbox.VectorModule.MeshGeneration;
using UnityEngine;

namespace Mapbox.VectorModule.ComponentSystem
{
    public class MapboxComponentsModule : VectorLayerModule
    {
        public MapboxComponentsModule(IMapInformation mapInformation, Source<VectorData> source,
            UnityContext unityContext, Dictionary<string, List<IVectorLayerVisualizer>> layerVisualizers,
            VectorModuleSettings vectorModuleSettings = null) : base(mapInformation, source, unityContext,
            layerVisualizers, vectorModuleSettings)
        {
        }

        protected override void MeshGeneration(VectorData data, Action<MeshGenerationTaskResult> callback)
        {
            MeshGenTaskWrapper<BuildingLayerTaskResult> meshTask = null;
            meshTask = new MeshGenTaskWrapper<BuildingLayerTaskResult>()
            {
                TileId = data.TileId,
                DataAction = () =>
                {
                    var result = new BuildingLayerTaskResult();
                    result.MeshData = new List<BuildingLayerDataResult>();
                    var decompressed = Compression.Decompress(data.Data);
                    var processedTile = new Data.VectorTile(decompressed);
                    try
                    {
                        foreach (var pair in _layerVisualizers)
                        {
                            foreach (var layerVisualizer in pair.Value)
                            {
                                var visualizer = (MapboxComponentVisualizer)layerVisualizer;
                                if (visualizer == null || !visualizer.Active ||
                                    visualizer.ContainsVisualFor(data.TileId))
                                    continue;

                                if (processedTile.TryGetLayer(visualizer.VectorLayerName, out var layer))
                                {
                                    var layerData = visualizer.CreateMesh(data.TileId, layer);
                                    result.MeshData.Add(new BuildingLayerDataResult()
                                    {
                                        LayerName = visualizer.VectorLayerName,
                                        LayerId = visualizer.VisualizerId,
                                        MeshData = layerData
                                    });
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
                        failResult.AddException(new Exception(string.Format("{0} mesh gen exception: {1}", data.TileId,
                            taskResult.ExceptionsAsString)));
                        callback(failResult);
                        return;
                    }

                    var resultGameObjects = new List<GameObject>();
                    foreach (var layerData in taskResult.MeshData)
                    {
                        // foreach (var vectorLayerVisualizers in _layerVisualizers
                        //              .Where(x => x.Value is MapboxComponentVisualizer && x.Key == layerData.LayerName)
                        //              .Select(x => x.Value))
                        IVectorLayerVisualizer visualizer = null;
                        var visualizers = _layerVisualizers[layerData.LayerName];
                        for (int i = 0; i < visualizers.Count; i++)
                        {
                            if (((MapboxComponentVisualizer)visualizers[i]).VisualizerId == layerData.LayerId)
                            {
                                visualizer = visualizers[i];
                                break;
                            }
                        }
                        if (visualizer == null)
                            continue;
                        
                        var layerVisualizer = (MapboxComponentVisualizer)visualizer;
                        if (layerData.MeshData == null)
                            continue;
                        var layerGameObjects = layerVisualizer.CreateGo(data.TileId, layerData.MeshData);
                        foreach (var gameObject in layerGameObjects)
                        {
                            gameObject.SetActive(false);
                            resultGameObjects.Add(gameObject);
                        }
                        
                    }

                    callback(new MeshGenerationTaskResult(TaskResultType.Success, resultGameObjects));
                }
            };
            if (!_activeTasks.ContainsKey(data.TileId)) _activeTasks.Add(data.TileId, new List<TaskWrapper>());
            _activeTasks[data.TileId].Add(meshTask);
            _unityContext.TaskManager.AddTask(meshTask, 0);
        }

        public class BuildingLayerTaskResult : MeshGenTaskWrapperResult
        {
            public List<BuildingLayerDataResult> MeshData;
        }

        public class BuildingLayerDataResult
        {
            public string LayerName;
            public MeshData MeshData;
            public int LayerId;
        }
    }
}
