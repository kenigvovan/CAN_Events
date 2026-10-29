using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Scoring
{
    // Small JSON stores under ModConfig/canevents/. Call Save while holding lockObj.
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
            var dir = ModPaths.Root;
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
                // Keep the bad file, the next save would overwrite it.
                string aside = JsonStore.MoveAside(filePath);
                logger?.Error("{0}: failed to load {1} - {2}. Starting EMPTY; the unreadable file was {3}.",
                    label, filePath, e.Message,
                    aside != null ? "kept as " + aside : "NOT backed up and will be overwritten on the next save");
                return new T();
            }
        }

        // Caller must hold lockObj.
        protected void Save() => JsonStore.WriteAtomic(filePath, Data, logger, label);
    }

    public static class JsonStore
    {
        // Temp file + replace, so a crash mid-write keeps the old file.
        public static void WriteAtomic(string path, object data, ILogger logger = null, string label = null)
        {
            if (!TryWriteAtomic(path, data, out var error))
                logger?.Warning("{0}: failed to save - {1}", label ?? path, error);
        }

        // Renames to "<name>.corrupt-<utc>", copies if rename fails. Null if nothing was kept.
        public static string MoveAside(string path)
        {
            string aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, aside);
                return aside;
            }
            catch
            {
                try
                {
                    File.Copy(path, aside, true);
                    return aside;
                }
                catch { return null; }
            }
        }

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
