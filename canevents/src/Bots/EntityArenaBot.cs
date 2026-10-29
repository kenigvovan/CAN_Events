using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace canevents.src.Bots
{
    // Seraph-shaped agent with EntityBehaviorSeraphInventory for gear.
    // Not EntityPlayerBot: its EntityAnimalBot command queue fights the AI tasks for controls.
    public class EntityArenaBot : EntityAgent
    {
        // InventoryGear: 0-14 worn gear, 15 right hand, 16 left hand.
        private const int RightHandSlotId = 15;
        private const int LeftHandSlotId = 16;

        private EntityBehaviorSeraphInventory inventoryBehavior;

        // StoreWithChunk left true: false made the server drop bodies the client still rendered.
        // Leftover bots are removed on load by BotCleanup.

        public override ItemSlot RightHandItemSlot
            => inventoryBehavior != null ? inventoryBehavior.Inventory[RightHandSlotId] : base.RightHandItemSlot;

        public override ItemSlot LeftHandItemSlot
            => inventoryBehavior != null ? inventoryBehavior.Inventory[LeftHandSlotId] : base.LeftHandItemSlot;

        // Server marks entities Inactive with no player nearby, which stops AI (bot-only matches froze).
        public override bool AlwaysActive { get => true; set { } }

        public override void Initialize(EntityProperties properties, ICoreAPI api, long chunkindex3d)
        {
            base.Initialize(properties, api, chunkindex3d);
            inventoryBehavior = GetBehavior<EntityBehaviorSeraphInventory>();
        }

        public override void OnEntitySpawn()
        {
            base.OnEntitySpawn();

            // Otherwise the held weapon isn't rendered.
            if (World.Side == EnumAppSide.Client
                && Properties.Client.Renderer is EntityShapeRenderer renderer)
            {
                renderer.DoRenderHeldItem = true;
            }
        }
    }
}
