using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace LagGridBroadcaster.InternalProfiler
{
    public interface IProfiler
    {
        void ReceiveProfilerResult(in ProfilerResult profilerResult);
    }

    public enum ProfilerCategory
    {
        General,
    }

    public readonly struct ProfilerToken
    {
        public readonly object GameEntity;
        public readonly ProfilerCategory Category;
        public readonly long StartTick;

        public ProfilerToken(object gameEntity, ProfilerCategory category)
        {
            GameEntity = gameEntity;
            Category = category;
            StartTick = Stopwatch.GetTimestamp();
        }
    }

    public readonly struct ProfilerResult
    {
        public readonly object GameEntity;
        public readonly ProfilerCategory Category;
        public readonly long TotalTick;
        public readonly bool IsMainThread;

        public ProfilerResult(in ProfilerToken token)
        {
            GameEntity = token.GameEntity;
            Category = token.Category;
            TotalTick = Stopwatch.GetTimestamp() - token.StartTick;
            IsMainThread = Thread.CurrentThread.ManagedThreadId == MySandboxGame.Static.UpdateThread.ManagedThreadId;
        }
    }

    public static class ProfilerResultQueue
    {
        static readonly object Gate = new object();
        static readonly object DispatchGate = new object();
        static readonly HashSet<IProfiler> Profilers = new HashSet<IProfiler>();

        public static IDisposable Profile(IProfiler observer)
        {
            lock (Gate)
                Profilers.Add(observer);
            return new RemoveAction(() =>
            {
                lock (Gate)
                    Profilers.Remove(observer);
            });
        }

        public static void Enqueue(in ProfilerResult result)
        {
            lock (DispatchGate)
            {
                IProfiler[] snapshot;
                lock (Gate)
                    snapshot = Profilers.ToArray();
                foreach (var profiler in snapshot)
                {
                    try
                    {
                        profiler.ReceiveProfilerResult(result);
                    }
                    catch
                    {
                        // Never let profiler exceptions bubble into the game loop.
                    }
                }
            }
        }

        sealed class RemoveAction : IDisposable
        {
            readonly Action _remove;
            public RemoveAction(Action remove) { _remove = remove; }
            public void Dispose() { _remove?.Invoke(); }
        }
    }

    public sealed class ProfilerEntry
    {
        long _rawMainThreadTime;
        long _rawOffThreadTime;

        public double MainThreadTime => _rawMainThreadTime * 1000.0D / Stopwatch.Frequency;
        public double OffThreadTime => _rawOffThreadTime * 1000.0D / Stopwatch.Frequency;
        public double TotalTime => MainThreadTime + OffThreadTime;

        internal void Add(in ProfilerResult profilerResult)
        {
            if (profilerResult.IsMainThread) _rawMainThreadTime += profilerResult.TotalTick;
            else _rawOffThreadTime += profilerResult.TotalTick;
        }

        internal void MergeWith(ProfilerEntry other)
        {
            _rawMainThreadTime += other._rawMainThreadTime;
            _rawOffThreadTime += other._rawOffThreadTime;
        }
    }

    public sealed class BaseProfilerResult<K>
    {
        readonly IReadOnlyDictionary<K, ProfilerEntry> _entities;

        internal BaseProfilerResult(ulong totalFrameCount, double totalTime, IReadOnlyDictionary<K, ProfilerEntry> self)
        {
            TotalFrameCount = totalFrameCount;
            TotalTime = totalTime;
            _entities = self;
        }

        public ulong TotalFrameCount { get; }
        public double TotalTime { get; }

        public bool TryGet(K key, out ProfilerEntry entity) => _entities.TryGetValue(key, out entity);

        public IEnumerable<KeyedEntity> GetTopEntities(int? limit = null)
        {
            return _entities.ToArray()
                .OrderByDescending(r => r.Value.TotalTime)
                .Select(kv => new KeyedEntity(kv.Key, kv.Value))
                .Take(limit ?? int.MaxValue)
                .ToArray();
        }

        public BaseProfilerResult<K1> MapKeys<K1>(Func<K, K1> f)
        {
            var mapped = new Dictionary<K1, ProfilerEntry>();
            foreach (var pair in _entities)
            {
                var newKey = f(pair.Key);
                if (mapped.TryGetValue(newKey, out var e)) e.MergeWith(pair.Value);
                else mapped[newKey] = pair.Value;
            }
            return new BaseProfilerResult<K1>(TotalFrameCount, TotalTime, mapped);
        }

        public readonly struct KeyedEntity
        {
            public readonly K Key;
            public readonly ProfilerEntry Entity;
            public KeyedEntity(K key, ProfilerEntry entity)
            {
                Key = key;
                Entity = entity;
            }
            public void Deconstruct(out K key, out ProfilerEntry entity)
            {
                key = Key;
                entity = Entity;
            }
        }
    }

    public abstract class BaseProfiler<K> : IProfiler, IDisposable
    {
        readonly ConcurrentDictionary<K, ProfilerEntry> _entries = new ConcurrentDictionary<K, ProfilerEntry>();
        readonly List<K> _tmpKeys = new List<K>();
        ulong _startFrameCount;
        DateTime _startTime;
        bool _ended;

        public virtual void MarkStart()
        {
            _startFrameCount = MySandboxGame.Static.SimulationFrameCounter;
            _startTime = DateTime.UtcNow;
        }

        public virtual void MarkEnd() => _ended = true;

        public void ReceiveProfilerResult(in ProfilerResult profilerResult)
        {
            if (_ended) return;
            try
            {
                Accept(profilerResult, _tmpKeys);
                foreach (var key in _tmpKeys)
                {
                    var entry = _entries.GetOrAdd(key, _ => new ProfilerEntry());
                    entry.Add(profilerResult);
                }
            }
            catch
            {
                // Swallow any per-sample failure to keep server stable.
            }
            finally
            {
                _tmpKeys.Clear();
            }
        }

        protected abstract void Accept(in ProfilerResult profilerResult, ICollection<K> acceptedKeys);

        public BaseProfilerResult<K> GetResult()
        {
            var totalFrames = MySandboxGame.Static.SimulationFrameCounter - _startFrameCount;
            var totalTime = (DateTime.UtcNow - _startTime).TotalMilliseconds;
            var copied = _entries.ToArray().ToDictionary(p => p.Key, p => p.Value);
            return new BaseProfilerResult<K>(totalFrames, totalTime, copied);
        }

        public virtual void Dispose() => _entries.Clear();
    }

    public sealed class GameEntityMask
    {
        readonly long? _playerMask;
        readonly long? _gridMask;
        readonly long? _factionMask;

        public GameEntityMask(long? playerMask = null, long? gridMask = null, long? factionMask = null)
        {
            _playerMask = playerMask;
            _gridMask = gridMask;
            _factionMask = factionMask;
        }

        public bool TestAll(MyCubeGrid grid)
        {
            if (_gridMask is { } gm && gm != grid.EntityId) return false;
            if (_playerMask is { } pm && !grid.BigOwners.Contains(pm)) return false;
            if (_factionMask is { } fm)
            {
                foreach (var owner in grid.BigOwners)
                {
                    var faction = MySession.Static.Factions.TryGetPlayerFaction(owner);
                    if (faction?.FactionId != fm) return false;
                }
            }
            return true;
        }

        public bool TestAll(IMyEntity entity) => entity is MyCubeGrid g ? TestAll(g) : true;
    }

    public sealed class GridProfiler : BaseProfiler<MyCubeGrid>
    {
        readonly GameEntityMask _mask;
        public GridProfiler(GameEntityMask mask) { _mask = mask; }

        protected override void Accept(in ProfilerResult profilerResult, ICollection<MyCubeGrid> acceptedKeys)
        {
            if (profilerResult.Category != ProfilerCategory.General) return;
            if (profilerResult.GameEntity is not IMyEntity entity) return;

            if (entity is MyCubeGrid grid)
            {
                if (_mask.TestAll(grid)) acceptedKeys.Add(grid);
                return;
            }

            var parent = entity.Parent;
            while (parent != null)
            {
                if (parent is MyCubeGrid parentGrid)
                {
                    if (_mask.TestAll(parentGrid)) acceptedKeys.Add(parentGrid);
                    break;
                }
                parent = parent.Parent;
            }
        }
    }

    public static class RuntimePatcher
    {
        static readonly Logger Log = LogManager.GetCurrentClassLogger();
        static readonly Type Self = typeof(RuntimePatcher);
        static readonly MethodInfo PrefixMethod = Self.GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo SuffixMethod = Self.GetMethod(nameof(Suffix), BindingFlags.NonPublic | BindingFlags.Static);

        static bool _patched;

        public static void PatchAll(PatchContext ctx)
        {
            if (_patched) return;
            var patched = 0;
            var coreTypes = new[]
            {
                typeof(MyCubeGrid),
                typeof(MyCubeBlock),
            };

            foreach (var type in coreTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    if (method.ReturnType != typeof(void)) continue;
                    var n = method.Name;
                    if (!(n.StartsWith("UpdateBeforeSimulation") || n.StartsWith("UpdateAfterSimulation") || n == "UpdateOnceBeforeFrame" || n == "Simulate"))
                        continue;

                    try
                    {
                        var pattern = ctx.GetPattern(method);
                        pattern.Prefixes.Add(PrefixMethod);
                        pattern.Suffixes.Add(SuffixMethod);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, $"Failed to patch {type.FullName}#{method.Name}");
                    }
                }
            }

            _patched = true;
            Log.Info($"LagGrid internal profiler patched {patched} update methods");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Prefix(object __instance, ref ProfilerToken? __localProfilerHandle)
        {
            __localProfilerHandle = new ProfilerToken(__instance, ProfilerCategory.General);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Suffix(ref ProfilerToken? __localProfilerHandle)
        {
            if (!__localProfilerHandle.HasValue) return;
            var result = new ProfilerResult(__localProfilerHandle.Value);
            ProfilerResultQueue.Enqueue(result);
        }
    }
}
