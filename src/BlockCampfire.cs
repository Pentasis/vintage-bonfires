using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Bonfires;

public class BlockCampfire : BlockFirepit, IIgnitable
{
    // Constants for gameplay values to improve readability and ease of modification.
    // We want 3 construction stages, each consuming 4 firewood, for a total of 12 firewood.
    private const int FIREWOOD_CONSUME_AMOUNT_PER_STAGE = 4;

    /// <summary>
    /// Hides the Stage property from BlockFirepit to define the campfire's construction stages.
    /// Campfire stages:
    /// 0: "base" (initial kindling block)
    /// 1: "construct1" (after 4 firewood)
    /// 2: "construct2" (after 8 firewood)
    /// 3: "construct3" (after 12 firewood)
    /// 4: "unlit" (fully constructed, ready to be lit)
    /// 5: "lit" or "extinct" (final states, handled by BlockFirepit's default logic)
    /// </summary>
    public new int Stage
    {
        get
        {
            return LastCodePart() switch
            {
                "base" => 0,
                "construct1" => 1,
                "construct2" => 2,
                "construct3" => 3,
                "unlit" => 4,
                _ => 5 // "lit" or "extinct"
            };
        }
    }

    /// <summary>
    /// Hides the NextStageCodePart property from BlockFirepit to define the campfire's construction progression.
    /// </summary>
    public new string NextStageCodePart
    {
        get
        {
            return LastCodePart() switch
            {
                "base" => "construct1",
                "construct1" => "construct2",
                "construct2" => "construct3",
                "construct3" => "unlit", // Final construction stage before being "unlit"
                _ => "unlit" // Default for any unexpected state, should lead to the final unlit stage
            };
        }
    }

    /// <summary>
    /// This method handles the construction of the campfire.
    /// It's a modified version of BlockFirepit.TryConstruct, adapted for campfire stages and fuel.
    /// This method is marked 'new' because the base TryConstruct is not virtual.
    /// </summary>
    public new bool TryConstruct(IWorldAccessor world, BlockPos pos, CollectibleObject obj, IPlayer player)
    {
        // Only allow construction with firewood
        if (obj.Code.Path != "firewood") return false;

        int currentStage = Stage;

        // If the campfire is already fully constructed (unlit) or beyond, construction is not possible.
        if (currentStage >= 4) return false; // 4 means "unlit"

        // Check if the player has enough firewood before proceeding with construction.
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative && player.InventoryManager.ActiveHotbarSlot.StackSize < FIREWOOD_CONSUME_AMOUNT_PER_STAGE)
        {
            if (api.Side == EnumAppSide.Client)
            {
                (api as ICoreClientAPI)?.TriggerIngameError(this, "notenough", Lang.Get("vintagebonfires:bonfire-notenoughfirewood", FIREWOOD_CONSUME_AMOUNT_PER_STAGE));
            }
            return true; // Prevents construction and consumption of the insufficient amount of firewood.
        }

        string nextStateCodePart = NextStageCodePart;
        
        // Exchange the current block for the next construction stage block.
        Block nextConstructionBlock = world.GetBlock(CodeWithParts(nextStateCodePart));
        if (nextConstructionBlock == null) return false; // Should not happen if variants are correctly defined

        world.BlockAccessor.ExchangeBlock(nextConstructionBlock.Id, pos);
        world.BlockAccessor.MarkBlockDirty(pos);
        if (nextConstructionBlock.Sounds != null) world.PlaySoundAt(nextConstructionBlock.Sounds.Place, pos, -0.5, player);

        // Consume firewood for construction.
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            player.InventoryManager.ActiveHotbarSlot.TakeOut(FIREWOOD_CONSUME_AMOUNT_PER_STAGE);
            player.InventoryManager.ActiveHotbarSlot.MarkDirty();
        }

        (player as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);

        return true;
    }

    /// <summary>
    /// Overrides OnBlockInteractStart to handle both construction and interaction with the completed campfire.
    /// This method is the entry point for player right-clicks on the block.
    /// </summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // Standard claim access check
        if (blockSel != null && !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
        {
            return false;
        }

        int currentStage = Stage;
        ItemStack stack = byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack;

        // --- Construction Logic ---
        // If the campfire is not yet fully constructed (stage < 4, i.e., "base", "construct1", "construct2", "construct3")
        if (currentStage < 4) // 4 means "unlit"
        {
            // If player is holding firewood, attempt to construct
            if (stack != null && stack.Collectible.Code.Path == "firewood" && TryConstruct(world, blockSel.Position, stack.Collectible, byPlayer))
            {
                // TryConstruct handles firewood consumption and animation
                return true;
            }
            // If not holding firewood, or construction failed, let the base class handle other potential interactions
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
        // --- Fully Constructed Campfire Interactions (unlit, lit, or extinct) ---
        else
        {
            // If the player is shift-clicking with an item, we need to filter fuel.
            if (byPlayer.Entity.Controls.ShiftKey && stack != null)
            {
                BlockEntityFirepit bef = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityFirepit;

                if (bef != null)
                {
                    bool activated = false;

                    // Allow any melting/cooking items into the input slot (e.g., pots, crucibles)
                    if (stack.Collectible.CombustibleProps != null && stack.Collectible.CombustibleProps.MeltingPoint > 0)
                    {
                        ItemStackMoveOperation op = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 1);
                        byPlayer.InventoryManager.ActiveHotbarSlot.TryPutInto(bef.inputSlot, ref op);
                        if (op.MovedQuantity > 0) activated = true;
                    }

                    // Only allow "firewood" into the fuel slot
                    if (stack.Collectible.Code.Path == "firewood" && stack.Collectible.CombustibleProps != null && stack.Collectible.CombustibleProps.BurnTemperature > 0)
                    {
                        ItemStackMoveOperation op = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 1);
                        byPlayer.InventoryManager.ActiveHotbarSlot.TryPutInto(bef.fuelSlot, ref op);
                        if (op.MovedQuantity > 0) activated = true;
                    }

                    if (activated)
                    {
                        (byPlayer as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
                        var loc = stack.ItemAttributes?["placeSound"].Exists == true ? AssetLocation.Create(stack.ItemAttributes["placeSound"].AsString(), stack.Collectible.Code.Domain) : null;
                        if (loc != null)
                        {
                            api.World.PlaySoundAt(loc.WithPathPrefixOnce("sounds/"), blockSel.Position.X, blockSel.Position.InternalY, blockSel.Position.Z, byPlayer, 0.88f + (float)api.World.Rand.NextDouble() * 0.24f, 16);
                        }
                        return true;
                    }
                }
            }

            // For other interactions (e.g., opening GUI, serving food from pots), delegate to base.
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }

    // --- IIgnitable Implementation ---

    /// <summary>
    /// Hides OnTryIgniteBlockOver to capture the player UID when the campfire is ignited.
    /// This UID is used for fire spreading claim checks.
    /// </summary>
    public new void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
    {
        // Call base method logic manually since we can't easily call base non-virtual method if we are re-implementing interface
        // Actually, since we inherit from BlockFirepit, we can call base.OnTryIgniteBlockOver directly.
        // The interface re-implementation just ensures THIS method is called by the engine.
        base.OnTryIgniteBlockOver(byEntity, pos, secondsIgniting, ref handling);

        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityCampfire becampfire)
        {
            if (byEntity is EntityPlayer player)
            {
                becampfire.StartedByPlayerUid = player.PlayerUID;
                becampfire.MarkDirty(true); // Ensure UID is saved and synced
            }
        }
    }

    public new EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
    {
        return base.OnTryIgniteBlock(byEntity, pos, secondsIgniting);
    }

    public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
    {
        // Re-implementing logic from BlockFirepit since it was explicit there
        BlockEntityFirepit bef = api.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityFirepit;
        if (bef != null && bef.IsBurning) return secondsIgniting > 2 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        return EnumIgniteState.NotIgnitable;
    }
}