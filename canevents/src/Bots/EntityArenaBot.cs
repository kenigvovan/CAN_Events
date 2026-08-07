using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    /// <summary>
    /// The body of an event bot: a seraph-shaped agent that wears and holds real items via
    /// EntityBehaviorSeraphInventory (the same 19-slot player gear inventory).
    /// Deliberately not the game's EntityPlayerBot: it derives from EntityAnimalBot, whose scripted
    /// command queue and path traversers would fight the AI tasks over the entity's controls.
    /// </summary>
    public class EntityArenaBot : EntityAgent
    {
        // InventoryGear slot layout: 0-14 worn gear, 15 right hand, 16 left hand.
        private const int RightHandSlotId = 15;
        private const int LeftHandSlotId = 16;

        private EntityBehaviorSeraphInventory inventoryBehavior;

        // StoreWithChunk stays at the base default (true): returning false made the server drop
        // bodies the client was still rendering, so the bot could not be hit. Bots that outlive
        // their match are cleaned up on load instead, see BotCleanup.

        public override ItemSlot RightHandItemSlot
            => inventoryBehavior != null ? inventoryBehavior.Inventory[RightHandSlotId] : base.RightHandItemSlot;

        public override ItemSlot LeftHandItemSlot
            => inventoryBehavior != null ? inventoryBehavior.Inventory[LeftHandSlotId] : base.LeftHandItemSlot;

        /// <summary>
        /// Arena bots never sleep: the server parks entities as Inactive with no player in
        /// simulation range, and an inactive entity runs no AI or physics (bot-only matches stood
        /// still forever). A match must run whether or not anyone is watching.
        /// </summary>
        public override bool AlwaysActive { get => true; set { } }

        public override void Initialize(EntityProperties properties, ICoreAPI api, long chunkindex3d)
        {
            base.Initialize(properties, api, chunkindex3d);
            inventoryBehavior = GetBehavior<EntityBehaviorSeraphInventory>();
        }

        public override void OnEntitySpawn()
        {
            base.OnEntitySpawn();

            // Without this the held weapon is invisible in the world and the bot swings empty-handed.
            if (World.Side == EnumAppSide.Client
                && Properties.Client.Renderer is EntityShapeRenderer renderer)
            {
                renderer.DoRenderHeldItem = true;
            }
        }
    }
}
