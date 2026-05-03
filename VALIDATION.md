## Runtime Validation Evidence

Environment used during validation:
- Torch: `v1.3.1.336-master`
- Space Engineers: `1.208.15.6`
- Plugin build: `LagGridBroadcaster (v9.9.11-local)`
- Log source: `Linux-SE-Tools/torch/Logs/Torch-2026-05-03.log`

### 1. Plugin loads correctly

```
20:29:08.0638 [INFO]   Torch.Managers.PluginManager: Loading plugin 'LagGridBroadcaster' (v9.9.11-local)
20:29:08.1338 [INFO]   LagGridBroadcaster.InternalProfiler.RuntimePatcher: LagGrid internal profiler patched 18 update methods
20:29:08.2888 [INFO]   Torch.Managers.PluginManager: Loaded 1 plugins.
```

### 2. Profiling command executes

```
20:32:11.6285 [INFO]   Chat:  (to Ruby): Profiling finish in 901ticks
20:32:11.6785 [INFO]   LagGridBroadcaster.LagGridBroadcasterCommands: Measure results saved to file
```

### 3. Result output is produced in chat

```
20:32:11.6825 [INFO]   Chat: LagGridBroadcaster (to Ruby): Faction top 1 grids:
20:32:11.6825 [INFO]   Chat: LagGridBroadcaster (to Ruby): KRI BEGONIA 7.3 (142us)
```

### 4. Repeated runs continue to work

Examples from earlier in the same log:

```
19:50:22.4860 [INFO]   Chat:  (to Ruby): Profiling finish in 900ticks
19:50:34.2520 [INFO]   Chat:  (to Ruby): Profiling finish in 60ticks
19:52:00.9678 [INFO]   Chat:  (to Ruby): Profiling finish in 59ticks
```

These entries verify that the embedded runtime path initializes and produces measurement output across multiple command runs.
