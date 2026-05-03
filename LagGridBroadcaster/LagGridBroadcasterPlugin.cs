using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

using LagGridBroadcaster.InternalProfiler;
using NLog;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace LagGridBroadcaster
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class LagGridBroadcasterPlugin : TorchPluginBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public readonly ConcurrentDictionary<long, List<IMyGps>> AddedGps =
            new ConcurrentDictionary<long, List<IMyGps>>();

        private Persistent<LagGridBroadcasterConfig> _config;
        private PatchManager _patchManager;
        private PatchContext _patchContext;


        public DateTime? LatestMeasureTime = null;

        //entityId to result
        public Dictionary<long, MeasureResult> LatestResults = null;
        public LagGridBroadcasterConfig Config => _config.Data;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();
            try
            {
                _patchManager = Torch.Managers.GetManager<PatchManager>();
                _patchContext = _patchManager.AcquireContext();
                RuntimePatcher.PatchAll(_patchContext);
                _patchManager.Commit();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to initialize internal profiler patches");
            }
        }

        private void SetupConfig()
        {
            var configFilePath = Path.Combine(StoragePath, $"{Name}.cfg");
            _config = Persistent<LagGridBroadcasterConfig>.Load(configFilePath);
        }

        public void Save()
        {
            try
            {
                _config.Save();
                Log.Info("Configuration Saved.");
            }
            catch (IOException e)
            {
                Log.Warn(e, "Configuration failed to save");
            }
        }
    }
}
