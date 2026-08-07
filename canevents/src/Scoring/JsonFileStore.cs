using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    // Base for the mod's small JSON-backed stores under ModConfig/canevents/. Writes are atomic (temp
    // file then replace), so a crash mid-write never leaves a corrupt live file. Subclasses keep their
    // data in Data and call Save while holding lockObj.
    public abstract class JsonFileStore<T> where T : class, new()
    {
        protected readonly object lockObj = new object();
        protected T Data;

        private readonly string filePath;
        private readonly ILogger logger;
        private readonly string label;

        protected JsonFileStore(ICoreServerAPI sapi, string fileName, string label)
        {
            this.logger = sapi.World.Logger;
            this.label = label;
            var dir = Path.Combine(GamePaths.ModConfig, "canevents");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            filePath = Path.Combine(dir, fileName);
            Data = Load();
        }

        private T Load()
        {
            if (!File.Exists(filePath)) return new T();
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<T>(json) ?? new T();
            }
            catch (Exception e)
            {
                logger?.Warning("{0}: failed to load - {1}", label, e.Message);
                return new T();
            }
        }

        // Caller must hold lockObj.
        protected void Save() => JsonStore.WriteAtomic(filePath, Data, logger, label);
    }

    /// <summary>
    /// Atomic JSON writing, separate from <see cref="JsonFileStore{T}"/> so stores that predate the base
    /// class or manage their own files (per-arena schematic rollback) get the guarantee without inheriting.
    /// </summary>
    public static class JsonStore
    {
        /// <summary>
        /// Serializes to a temp file, then replaces the target in one step, so a crash mid-write leaves
        /// the previous version intact rather than a half-written live file.
        /// </summary>
        public static void WriteAtomic(string path, object data, ILogger logger = null, string label = null)
        {
            if (!TryWriteAtomic(path, data, out var error))
                logger?.Warning("{0}: failed to save - {1}", label ?? path, error);
        }

        /// <summary>Same write, for callers that must report the failure to a player rather than log it.</summary>
        public static bool TryWriteAtomic(string path, object data, out string error)
        {
            error = null;
            string tmp = path + ".tmp";
            try
            {
                var json = data is Newtonsoft.Json.Linq.JToken token
                    ? token.ToString(Formatting.Indented)
                    : JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
