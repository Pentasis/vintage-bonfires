using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Bonfires
{
    public class BlockBonfire : Block, IIgnitable
    {
        // Constants for magic numbers and string literals
        private const float IGNITE_SECONDS = 3f;
        private const string FIREWOOD_ITEM_CODE = "firewood";
        private const string BURNSTATE_VARIANT_CODE = "burnstate";
        private const int FIREWOOD_CONSUME_AMOUNT = 1;

        private WorldInteraction[] _interactions = System.Array.Empty<WorldInteraction>();

        // Stage property now returns an EBonfireStage enum for better type safety and readability.
        public EBonfireStage Stage
        {
            get
            {
                return LastCodePart() switch
                {
                    "extinct" => EBonfireStage.Extinct,
                    "base" => EBonfireStage.Base,
                    "construct1" => EBonfireStage.Construct1,
                    "construct2" => EBonfireStage.Construct2,
                    "construct3" => EBonfireStage.Construct3,
                    _ => EBonfireStage.Extinct // Default for unknown states
                };
            }
        }

        public override void OnLoaded(ICoreAPI coreApi)
        {
            base.OnLoaded(coreApi);

            var canIgniteStacks = new List<ItemStack>();
            var fuelStacks = new List<ItemStack>();

            foreach (CollectibleObject obj in coreApi.World.Collectibles)
            {
                if ((obj is Block block && block.HasBehavior<BlockBehaviorCanIgnite>()) || obj is ItemFirestarter)
                {
                    var stacks = obj.GetHandBookStacks(coreApi as ICoreClientAPI);
                    if (stacks != null) canIgniteStacks.AddRange(stacks);
                }

                if (obj is Item item && item.Code.Path == FIREWOOD_ITEM_CODE)
                {
                    var stacks = obj.GetHandBookStacks(coreApi as ICoreClientAPI);
                    if (stacks != null) fuelStacks.AddRange(stacks);
                }
            }

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
                        // Can only ignite if it has fuel and is not already burning
                        return bef != null && bef.TotalFuel > 0 && !bef.Burning ? wi.Itemstacks : null;
                    }
                },
                new WorldInteraction
                {
                    ActionLangCode = "bonfires-return:blockhelp-bonfire-fuel",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = fuelStacks.ToArray(),
                    GetMatchingStacks = (wi, bs, _) =>
                    {
                        var currentBlockInWorld = coreApi.World.BlockAccessor.GetBlock(bs.Position);
                        var bef = coreApi.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityBonfire;
                        
                        // Get the Stage from the actual block in the world
                        var currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? EBonfireStage.Extinct;

                        // Show "Add Fuel" if it's not burning and either:
                        // 1. It's in a construction stage
                        // 2. It's fully constructed and not full of fuel
                        return !currentBlockInWorld.LastCodePart().Equals("lit") && bef != null &&
                               (currentStage < EBonfireStage.Construct3 || (currentStage >= EBonfireStage.Construct3 && bef.TotalFuel < BlockEntityBonfire.MAX_FUEL))
                               ? wi.Itemstacks : null;
                    }
                }
            };
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return _interactions.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }

        public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire { Burning: true })
            {
                return EnumIgniteState.NotIgnitablePreventDefault;
            }

            return secondsIgniting > IGNITE_SECONDS ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }

        public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire bef && !bef.Burning)
            {
                if (byEntity is EntityPlayer player)
                {
                    bef.Ignite(player.PlayerUID);
                }
            }
            handling = EnumHandling.PreventDefault;
        }

        public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityBonfire { Burning: true })
            {
                return EnumIgniteState.NotIgnitablePreventDefault;
            }

            return secondsIgniting > IGNITE_SECONDS ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }


        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            var hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            // Only proceed if holding firewood
            if (hotbarSlot?.Itemstack?.Collectible.Code.Path != FIREWOOD_ITEM_CODE)
                return base.OnBlockInteractStart(world, byPlayer, blockSel);

            BlockPos pos = blockSel.Position;
            // Get the actual block instance in the world at this position
            Block currentBlockInWorld = world.BlockAccessor.GetBlock(pos);
            string currentCodePart = currentBlockInWorld.LastCodePart(); // Use the actual block's code part

            // Get the BlockEntityBonfire
            BlockEntityBonfire? bef = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityBonfire;
            if (bef == null) return base.OnBlockInteractStart(world, byPlayer, blockSel); // Should not happen if it's a BlockBonfire

            // Determine current stage using the Stage property of the current block in world
            var currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? EBonfireStage.Extinct;

            // Handle construction stages (Extinct, Base, Construct1, Construct2)
            if (currentStage < EBonfireStage.Construct3) 
            {
                // Determine the next state based on the current block's code part
                string nextStateCodePart;
                switch (currentCodePart)
                {
                    case "extinct":
                    case "base":
                        nextStateCodePart = "construct1";
                        break;
                    case "construct1":
                        nextStateCodePart = "construct2";
                        break;
                    case "construct2":
                        nextStateCodePart = "construct3";
                        break;
                    default:
                        // This case should ideally not be reached
                        return base.OnBlockInteractStart(world, byPlayer, blockSel);
                }

                Block nextConstructionBlock = world.GetBlock(currentBlockInWorld.CodeWithVariant(BURNSTATE_VARIANT_CODE, nextStateCodePart));
                world.BlockAccessor.ExchangeBlock(nextConstructionBlock.Id, pos);
                world.BlockAccessor.MarkBlockDirty(pos);
                if (nextConstructionBlock.Sounds != null) world.PlaySoundAt(nextConstructionBlock.Sounds.Place, pos.X, pos.Y, pos.Z, byPlayer);

                if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                {
                    hotbarSlot.TakeOut(FIREWOOD_CONSUME_AMOUNT); // Consume 1 firewood for construction
                    hotbarSlot.MarkDirty();
                }
                return true;
            }
            // Handle fueling phase (Stage: Construct3)
            else if (currentStage >= EBonfireStage.Construct3) // This covers the fully constructed state
            {
                if (bef.Refuel(FIREWOOD_CONSUME_AMOUNT))
                {
                    // No visual change for the block itself here, it remains construct3
                    if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    {
                        hotbarSlot.TakeOut(FIREWOOD_CONSUME_AMOUNT); // Consume 1 firewood for fuel
                        hotbarSlot.MarkDirty();
                    }
                    return true;
                }
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}