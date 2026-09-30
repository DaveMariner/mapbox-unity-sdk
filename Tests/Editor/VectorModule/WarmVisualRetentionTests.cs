using System;
using System.Collections;
using System.Collections.Generic;
using Mapbox.BaseModule.Data;
using Mapbox.BaseModule.Data.DataFetchers;
using Mapbox.BaseModule.Data.Interfaces;
using Mapbox.BaseModule.Data.Tasks;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Utilities;
using Mapbox.VectorModule;
using Mapbox.VectorModule.MeshGeneration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mapbox.VectorModuleTests
{
    public class WarmVisualRetentionTests
    {
        private static readonly CanonicalTileId A = new CanonicalTileId(14, 100, 100);
        private static readonly CanonicalTileId B = new CanonicalTileId(14, 101, 100);
        private static readonly CanonicalTileId C = new CanonicalTileId(14, 102, 100);
        private static readonly CanonicalTileId D = new CanonicalTileId(14, 103, 100);
        private static readonly CanonicalTileId E = new CanonicalTileId(14, 104, 100);

        [Test]
        public void WarmTileReactivatesSameObjectWithoutRegeneration()
        {
            using (var fixture = new Fixture(2))
            {
                fixture.Generate(A);
                fixture.Keep(A);
                var original = fixture.Module.Visuals[A];
                fixture.Keep();
                Assert.IsTrue(fixture.Visualizer.IsWarm(A));
                fixture.Keep(A);

                Assert.AreSame(original, fixture.Module.Visuals[A]);
                Assert.AreEqual(1, fixture.Module.GenerationCount);
                Assert.IsTrue(original.activeSelf);
                Assert.AreEqual(2, fixture.Visualizer.PositionCount(A),
                    "The initial generation and warm-hit activation must both position the visual.");
            }
        }

        [Test]
        public void WarmCapacityEvictsLeastRecentlyUsedTileAndUnregistersOnce()
        {
            using (var fixture = new Fixture(2))
            {
                fixture.Generate(A, B, C, D);
                fixture.Keep(A, B, C, D);
                fixture.Keep(A);
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(B));

                fixture.Keep(A, D);
                fixture.Keep(A);
                fixture.Generate(E);
                fixture.Keep(A, E);
                fixture.Keep(A);

                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(C));
                Assert.AreEqual(0, fixture.Visualizer.UnregisterCount(D));
                Assert.AreEqual(0, fixture.Visualizer.UnregisterCount(E));
                fixture.Source.DisposeTile(B);
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(B));
            }
        }

        [Test]
        public void ZeroCapacityKeepsDestructiveLegacyUnload()
        {
            using (var fixture = new Fixture(0))
            {
                fixture.Generate(A);
                fixture.Keep(A);
                fixture.Keep();
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(A));
                Assert.IsFalse(fixture.Visualizer.IsWarm(A));
            }
        }

        [Test]
        public void SourceCacheDisposalDoesNotEvictWarmVisual()
        {
            using (var fixture = new Fixture(2))
            {
                fixture.Generate(A);
                fixture.Keep(A);
                fixture.Keep();
                var original = fixture.Module.Visuals[A];
                fixture.Source.DisposeTile(A);
                Assert.AreEqual(0, fixture.Visualizer.UnregisterCount(A));
                fixture.Keep(A);
                Assert.AreSame(original, fixture.Module.Visuals[A]);
                Assert.AreEqual(1, fixture.Module.GenerationCount);
            }
        }

        [Test]
        public void ReloadAndTeardownRemoveWarmStateAndDetachSourceCallback()
        {
            using (var fixture = new Fixture(2))
            {
                fixture.Generate(A);
                fixture.Keep(A);
                fixture.Keep();
                fixture.Module.ReloadTile(A);
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(A));
                Assert.AreEqual(2, fixture.Module.GenerationCount);
                fixture.Keep();
                fixture.Module.OnDestroy();
                fixture.Module.OnDestroy();
                fixture.Source.DisposeTile(A);
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(A));
            }
        }

        [UnityTest]
        public IEnumerator CancelledGenerationCannotPublishAnOrphanAfterCacheDisposal()
        {
            using (var fixture = new Fixture(2, deferGeneration: true))
            {
                var routine = fixture.Module.ProcessTileData(A);
                Assert.IsTrue(routine.MoveNext());
                var generation = routine.Current as IEnumerator;
                Assert.IsNotNull(generation);
                Assert.IsTrue(generation.MoveNext());
                fixture.Source.DisposeTile(A);
                fixture.Module.CompletePendingGeneration(A);
                while (generation.MoveNext()) { }
                Assert.IsFalse(routine.MoveNext());
                yield return null;

                Assert.IsFalse(new List<CanonicalTileId>(fixture.Module.GetReadyTiles()).Contains(A));
                Assert.AreEqual(0, fixture.Module.PublishedCount);
                Assert.IsTrue(fixture.Module.Visuals[A] == null);
            }
        }

        [Test]
        public void RapidReturnDuringPendingGenerationJoinsExistingWork()
        {
            using (var fixture = new Fixture(2, deferGeneration: true))
            {
                var firstRequest = fixture.Module.ProcessTileData(A);
                Assert.IsTrue(firstRequest.MoveNext());
                var firstGeneration = firstRequest.Current as IEnumerator;
                Assert.IsNotNull(firstGeneration);
                Assert.IsTrue(firstGeneration.MoveNext());

                fixture.Keep();
                var returnedRequest = fixture.Module.ProcessTileData(A);
                Assert.IsTrue(returnedRequest.MoveNext());
                var returnedGeneration = returnedRequest.Current as IEnumerator;
                Assert.IsNotNull(returnedGeneration);
                Assert.IsTrue(returnedGeneration.MoveNext());
                fixture.Keep(A);

                fixture.Module.CompletePendingGeneration(A);
                while (firstGeneration.MoveNext()) { }
                while (returnedGeneration.MoveNext()) { }

                Assert.IsFalse(firstRequest.MoveNext());
                Assert.IsFalse(returnedRequest.MoveNext());
                Assert.AreEqual(1, fixture.Module.GenerationCount);
                Assert.AreEqual(1, fixture.Module.PublishedCount);
                Assert.IsTrue(fixture.Module.Visuals[A] != null);
            }
        }

        [Test]
        public void MixedUnsupportedVisualizerDisablesWarmRetentionForWholeModule()
        {
            using (var fixture = new Fixture(2, includeUnsupportedCollector: true))
            {
                fixture.Generate(A);
                fixture.Keep(A);
                fixture.Keep();
                Assert.AreEqual(1, fixture.Visualizer.UnregisterCount(A));
                Assert.AreEqual(1, fixture.Collector.UnregisterCount(A));
                Assert.IsFalse(fixture.Visualizer.IsWarm(A));
            }
        }

        [Test]
        public void FullRotationCoverageRevisitsWarmSetsWithoutNewObjects()
        {
            using (var fixture = new Fixture(4))
            {
                fixture.Generate(A, B);
                fixture.Keep(A, B);
                var initialA = fixture.Module.Visuals[A];
                var initialB = fixture.Module.Visuals[B];
                fixture.Generate(C);
                fixture.Keep(B, C);
                var initialC = fixture.Module.Visuals[C];
                fixture.Generate(D);
                fixture.Keep(C, D);
                var initialD = fixture.Module.Visuals[D];
                fixture.Keep(D, A);
                fixture.Keep(A, B);

                Assert.AreEqual(4, fixture.Module.GenerationCount);
                Assert.AreSame(initialA, fixture.Module.Visuals[A]);
                Assert.AreSame(initialB, fixture.Module.Visuals[B]);
                Assert.AreSame(initialC, fixture.Module.Visuals[C]);
                Assert.AreSame(initialD, fixture.Module.Visuals[D]);
                Assert.IsTrue(initialA.activeSelf);
                Assert.IsTrue(initialB.activeSelf);
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly RecordingSource Source = new RecordingSource();
            public readonly RecordingVisualizer Visualizer;
            public readonly RecordingCollector Collector;
            public readonly RecordingModule Module;

            public Fixture(int capacity, bool includeUnsupportedCollector = false, bool deferGeneration = false)
            {
                Visualizer = new RecordingVisualizer();
                Collector = includeUnsupportedCollector ? new RecordingCollector() : null;
                var visualizers = new List<IVectorLayerVisualizer> { Visualizer };
                if (Collector != null) visualizers.Add(Collector);
                Module = new RecordingModule(Source, Visualizer,
                    new Dictionary<string, List<IVectorLayerVisualizer>> { { "test", visualizers } }, capacity,
                    deferGeneration);
            }

            public void Generate(params CanonicalTileId[] tileIds)
            {
                foreach (var tileId in tileIds)
                {
                    var routine = Module.ProcessTileData(tileId);
                    while (routine.MoveNext()) { }
                }
            }

            public void Keep(params CanonicalTileId[] tileIds)
            {
                Module.RetainTiles(new HashSet<CanonicalTileId>(tileIds));
            }

            public void Dispose()
            {
                Module.OnDestroy();
                Visualizer.DisposeVisuals();
            }
        }

        private sealed class RecordingSource : Source<VectorData>
        {
            public override bool IsReady() => true;
            public override bool RetainTiles(HashSet<CanonicalTileId> retainedTiles) => true;
            public override bool CheckInstantData(CanonicalTileId tileIdCanonical) => true;
            public override bool GetInstantData(CanonicalTileId requestedDataTileId, out VectorData data)
            {
                data = new VectorData { TileId = requestedDataTileId };
                return true;
            }
            public void DisposeTile(CanonicalTileId tileId) => CacheItemDisposed(tileId);
        }

        private sealed class RecordingModule : VectorLayerModule
        {
            private readonly RecordingVisualizer _visualizer;
            private bool _deferGeneration;
            private Action<MeshGenerationTaskResult> _pendingGeneration;
            public readonly Dictionary<CanonicalTileId, GameObject> Visuals = new Dictionary<CanonicalTileId, GameObject>();
            public int GenerationCount { get; private set; }
            public int PublishedCount { get; private set; }

            public RecordingModule(RecordingSource source, RecordingVisualizer visualizer,
                Dictionary<string, List<IVectorLayerVisualizer>> visualizers, int capacity, bool deferGeneration)
                : base(null, source, null, visualizers, new VectorModuleSettings
                {
                    DataSettings = new VectorSourceSettings { ClampDataLevelToMax = 16 },
                    RejectTilesOutsideZoom = new Vector2(0, 20),
                    WarmVisualTileCapacity = capacity
                })
            {
                _visualizer = visualizer;
                _deferGeneration = deferGeneration;
                OnVectorMeshCreated += (tileId, objects) => PublishedCount++;
            }

            protected override void MeshGeneration(VectorData data, Action<MeshGenerationTaskResult> callback)
            {
                GenerationCount++;
                if (_deferGeneration)
                {
                    _pendingGeneration = callback;
                    return;
                }
                CompleteGeneration(data.TileId, callback);
            }

            public void CompletePendingGeneration(CanonicalTileId tileId)
            {
                var callback = _pendingGeneration;
                _pendingGeneration = null;
                CompleteGeneration(tileId, callback);
            }

            private void CompleteGeneration(CanonicalTileId tileId, Action<MeshGenerationTaskResult> callback)
            {
                var visual = new GameObject(tileId.ToString());
                visual.SetActive(false);
                Visuals[tileId] = visual;
                _visualizer.Attach(tileId, visual);
                callback(new MeshGenerationTaskResult(TaskResultType.Success, new[] { visual }));
            }
        }

        private sealed class RecordingVisualizer : VectorLayerVisualizer, IDisposable
        {
            private readonly Dictionary<CanonicalTileId, GameObject> _visuals = new Dictionary<CanonicalTileId, GameObject>();
            private readonly HashSet<CanonicalTileId> _warm = new HashSet<CanonicalTileId>();
            private readonly Dictionary<CanonicalTileId, int> _unregisterCounts = new Dictionary<CanonicalTileId, int>();
            private readonly Dictionary<CanonicalTileId, int> _positionCounts = new Dictionary<CanonicalTileId, int>();

            public RecordingVisualizer() : base("warm retention tests", null) { }
            public void Attach(CanonicalTileId tileId, GameObject visual) => _visuals[tileId] = visual;
            public bool IsWarm(CanonicalTileId tileId) => _warm.Contains(tileId);
            public int UnregisterCount(CanonicalTileId tileId) => _unregisterCounts.TryGetValue(tileId, out var count) ? count : 0;
            public int PositionCount(CanonicalTileId tileId) => _positionCounts.TryGetValue(tileId, out var count) ? count : 0;

            public override void UpdateForView(CanonicalTileId tileId, IMapInformation information)
            {
                _positionCounts[tileId] = PositionCount(tileId) + 1;
            }

            public override void SetActive(CanonicalTileId tileId, bool isActive, IMapInformation mapInformation)
            {
                if (_visuals.TryGetValue(tileId, out var visual)) visual.SetActive(isActive);
            }

            public override void DeactivateWarm(CanonicalTileId tileId, IMapInformation mapInformation)
            {
                _warm.Add(tileId);
                if (_visuals.TryGetValue(tileId, out var visual)) visual.SetActive(false);
            }

            public override void ReactivateWarm(CanonicalTileId tileId, IMapInformation mapInformation)
            {
                _warm.Remove(tileId);
                if (_visuals.TryGetValue(tileId, out var visual)) visual.SetActive(true);
            }

            public override void UnregisterTile(CanonicalTileId tileId)
            {
                _unregisterCounts[tileId] = UnregisterCount(tileId) + 1;
                _warm.Remove(tileId);
                if (_visuals.TryGetValue(tileId, out var visual))
                {
                    UnityEngine.Object.DestroyImmediate(visual);
                    _visuals.Remove(tileId);
                }
            }

            public void DisposeVisuals()
            {
                foreach (var visual in _visuals.Values)
                {
                    if (visual != null) UnityEngine.Object.DestroyImmediate(visual);
                }
                _visuals.Clear();
            }

            public void Dispose() => DisposeVisuals();
        }

        private sealed class RecordingCollector : IVectorLayerVisualizer
        {
            private int _unregisterCount;
            public string VectorLayerName => "test";
            public bool Active { get; set; } = true;
            public Dictionary<int, ModifierStack> GetModStacks => new Dictionary<int, ModifierStack>();
            public int UnregisterCount(CanonicalTileId tileId) => _unregisterCount;
            public void AddModifierStack(List<ModifierStack> stack) { }
            public Dictionary<int, HashSet<MeshData>> CreateMesh(CanonicalTileId tileId, Mapbox.VectorTile.VectorTileLayer layer) => null;
            public List<GameObject> CreateGo(CanonicalTileId tileId, Dictionary<int, HashSet<MeshData>> meshData) => null;
            public void UnregisterTile(CanonicalTileId tileId) => _unregisterCount++;
            public IEnumerator Initialize() { yield break; }
            public void OnDestroy() { }
            public void UpdateForView(CanonicalTileId canonicalTileId, IMapInformation information) { }
            public void SetActive(CanonicalTileId tileId, bool isActive, IMapInformation mapInformation) { }
            public bool ContainsVisualFor(CanonicalTileId dataTileId) => false;
        }
    }
}
