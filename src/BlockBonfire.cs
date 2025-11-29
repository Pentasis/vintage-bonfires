using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Bonfires
{
    /// <summary>
    /// This class defines the behavior of the bonfire block itself, primarily handling player interactions,
    /// state transitions between construction stages, and the ignition process.
    /// </summary>
    public class BlockBonfire : Block, IIgnitable
    {
        // Constants for gameplay values to improve readability and ease of modification.
        private const float IGNITE_SECONDS = 3f;
        private const int FIREWOOD_CONSUME_AMOUNT = 1;

        private WorldInteraction[] _interactions = System.Array.Empty<WorldInteraction>();

        /// <summary>
        /// Determines the current EBonfireStage of the block based on its "burnstate" variant.
        /// This property is crucial for state-dependent logic throughout the class.
        /// </summary>
        public EBonfireStage Stage
        {
            get
            {
                return LastCodePart() switch
                {
                    "base" => EBonfireStage.Base,
                    "construct1" => EBonfireStage.Construct1,
                    "construct2" => EBonfireStage.Construct2,
                    "construct3" => EBonfireStage.Construct3,
                    "lit" => EBonfireStage.Lit,
                    "extinct" => EBonfireStage.Extinct,
                    _ => EBonfireStage.Extinct // Default to Extinct for any unknown or mismatched states.
                };
            }
        }

        /// <summary>
        /// Called when the block is loaded by the game. Initializes the interaction help text
        /// that players see when looking at the bonfire.
        /// </summary>
        public override void OnLoaded(ICoreAPI coreApi)
        {
            base.OnLoaded(coreApi);

            var canIgniteStacks = new List<ItemStack>();
            var fuelStacks = new List<ItemStack>();

            // Populate lists for items that can ignite or be used as fuel.
            foreach (CollectibleObject obj in coreApi.World.Collectibles)
            {
                if ((obj is Block block && block.HasBehavior<BlockBehaviorCanIgnite>()) || obj is ItemFirestarter)
                {
                    var stacks = obj.GetHandBookStacks(coreApi as ICoreClientAPI);
                    if (stacks != null) canIgniteStacks.AddRange(stacks);
                }

                if (obj is Item item && item.Code.Path == "firewood")
                {
                    var stacks = obj.GetHandBookStacks(coreApi as ICoreClientAPI);
                    if (stacks != null) fuelStacks.AddRange(stacks);
                }
            }

            // Define the dynamic interaction help prompts.
            _interactions = new[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "blockhelp-firepit-ignite",
                    MouseButton = EnumMouseButton.Right,
                    HotKeyCode = "sneak",
                    Itemstacks = canIgniteStacks.ToArray(),
                    GetMatchingStacks = (wi, bs, _) =>
                    {
                        var bef = coreApi.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityBonfire;
                        // The "Ignite" help text should only appear if the bonfire has fuel and is not already burning.
                        return bef != null && bef.TotalFuel > 0 && !bef.Burning ? wi.Itemstacks : null;
                    }
                },
                new WorldInteraction
                {
                    ActionLangCode = "vintage-bonfires:blockhelp-bonfire-fuel",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = fuelStacks.ToArray(),
                    GetMatchingStacks = (wi, bs, _) =>
                    {
                        var currentBlockInWorld = coreApi.World.BlockAccessor.GetBlock(bs.Position);
                        var bef = coreApi.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityBonfire;
                        
                        var currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? EBonfireStage.Extinct;

                        // The "Add Fuel" help text should appear during construction or when fueling a completed (but not full) bonfire.
                        // It should not appear if the bonfire is lit.
                        bool isConstructing = currentStage == EBonfireStage.Base || currentStage == EBonfireStage.Construct1 || currentStage == EBonfireStage.Construct2 || currentStage == EBonfireStage.Extinct;
                        bool canFuel = currentStage == EBonfireStage.Construct3 && bef != null && bef.TotalFuel < BlockEntityBonfire.MAX_FUEL;

                        return currentStage != EBonfireStage.Lit && (isConstructing || canFuel) ? wi.Itemstacks : null;
                    }
                }
            };
        }

        /// <summary>
        /// Provides the interaction help text to the player.
        /// </summary>
        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return _interactions.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }

        /// <summary>
        /// Part of the IIgnitable interface. Determines if the block can be ignited.
        /// </summary>
        public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
        {
            // Prevent ignition if the bonfire is already burning.
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire { Burning: true })
            {
                return EnumIgniteState.NotIgnitablePreventDefault;
            }

            // The block will ignite after being exposed to fire for a set duration.
            return secondsIgniting > IGNITE_SECONDS ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }

        /// <summary>
        /// Part of the IIgnitable interface. Called when the block has been successfully ignited.
        /// </summary>
        public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire bef && !bef.Burning)
            {
                if (byEntity is EntityPlayer player)
                {
                    bef.Ignite(player.PlayerUID);
                }
            }
            handling = EnumHandling.PreventDefault; // Prevents default game behavior.
        }

        /// <summary>
        /// Part of the IIgnitable interface. Handles ignition via an item (like a torch).
        /// </summary>
        public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire { Burning: true })
            {
                return EnumIgniteState.NotIgnitablePreventDefault;
            }

            return secondsIgniting > IGNITE_SECONDS ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }

        /// <summary>
        /// This is the core method for player interaction. It's called when a player right-clicks the bonfire.
        /// It handles advancing the construction stage and adding fuel.
        /// </summary>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            var hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            // Interaction is only relevant if the player is holding firewood.
            if (hotbarSlot?.Itemstack?.Collectible.Code.Path != "firewood")
                return base.OnBlockInteractStart(world, byPlayer, blockSel);

            BlockPos pos = blockSel.Position;
            Block currentBlockInWorld = world.BlockAccessor.GetBlock(pos);

            BlockEntityBonfire? bef = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityBonfire;
            if (bef == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            var currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? EBonfireStage.Extinct;

            // Do not allow adding firewood if the bonfire is already lit.
            if (currentStage == EBonfireStage.Lit)
            {
                return base.OnBlockInteractStart(world, byPlayer, blockSel);
            }

            // --- Construction Logic ---
            // If the bonfire is in any non-fuelable, non-lit state, interacting with firewood will advance its construction.
            if (currentStage == EBonfireStage.Base || currentStage == EBonfireStage.Construct1 || currentStage == EBonfireStage.Construct2 || currentStage == EBonfireStage.Extinct)
            {
                string nextStateCodePart;
                switch (currentStage)
                {
                    case EBonfireStage.Extinct:
                    case EBonfireStage.Base:
                        nextStateCodePart = "construct1";
                        break;
                    case EBonfireStage.Construct1:
                        nextStateCodePart = "construct2";
                        break;
                    case EBonfireStage.Construct2:
                        nextStateCodePart = "construct3";
                        break;
                    default:
                        return base.OnBlockInteractStart(world, byPlayer, blockSel); // Should not be reached.
                }

                // Exchange the current block for the next construction stage block.
                Block nextConstructionBlock = world.GetBlock(currentBlockInWorld.CodeWithVariant("burnstate", nextStateCodePart));
                world.BlockAccessor.ExchangeBlock(nextConstructionBlock.Id, pos);
                world.BlockAccessor.MarkBlockDirty(pos);
                if (nextConstructionBlock.Sounds != null) world.PlaySoundAt(nextConstructionBlock.Sounds.Place, pos.X, pos.Y, pos.Z, byPlayer);

                // Consume one piece of firewood for construction.
                if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                {
                    hotbarSlot.TakeOut(FIREWOOD_CONSUME_AMOUNT);
                    hotbarSlot.MarkDirty();
                }
                return true; // Interaction handled.
            }
            
            // --- Fueling Logic ---
            // If the bonfire is fully constructed, interacting with firewood adds fuel.
            else if (currentStage == EBonfireStage.Construct3)
            {
                if (bef.Refuel(FIREWOOD_CONSUME_AMOUNT))
                {
                    // Consume one piece of firewood for fuel.
                    if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    {
                        hotbarSlot.TakeOut(FIREWOOD_CONSUME_AMOUNT);
                        hotbarSlot.MarkDirty();
                    }
                    return true; // Interaction handled.
                }
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}