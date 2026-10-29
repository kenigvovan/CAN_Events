using System;
using System.IO;

namespace canevents.src.EventPlacesManagers
{
    // Writes arena templates to ModConfig/canevents/arenas, where they override the shipped ones.
    public static class TemplateStore
    {
        // Atomic write; false with the reason when it failed.
        public static bool Write(ArenaTemplate template, out string error)
        {
            try
            {
                Directory.CreateDirectory(ModPaths.Arenas);
                return Scoring.JsonStore.TryWriteAtomic(ModPaths.ArenaFile(template.Code), template, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
