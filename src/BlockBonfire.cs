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
        private WorldInteraction[] _interactions = System.Array.Empty<WorldInteraction>();

        // Stage property now correctly reflects the state of the *block definition*
        public int Stage
        {
            get
            {
                return LastCodePart() switch
                {
                    "construct1" => 1,
                    "construct2" => 2,
                    "construct3" => 3,
                    "cold" => 4, // Fully constructed, ready for fuel
                    "extinct" => 0, // Burned out, needs to be rebuilt
                    _ => 0 // Default for unknown states, treat as needing construction
                };
            }
        }

        // NextStageCodePart now correctly reflects the state of the *block definition*
        // This property will primarily be used for advancing construction stages (0-2)
        public string NextStageCodePart
        {
            get
            {
                return LastCodePart() switch
                {
                    "cold" => "construct1",
                    "extinct" => "construct1", // Extinct bonfires also start construction from stage 1
                    "construct1" => "construct2",
                    "construct2" => "construct3",
                    // If it's construct3, the visual state should not change, but fuel is added.
                    // This case is handled explicitly in OnBlockInteractStart, not by this property.
                    _ => "cold" // Fallback, though should be handled by OnBlockInteractStart
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

                if (obj is Item item && item.Code.Path == "firewood")
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
                        int currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? 0;

                        // Show "Add Fuel" if it's not burning and either:
                        // 1. It's in a construction stage (Stage 0-3)
                        // 2. It's fully constructed (Stage 4, 'cold' state) and not full of fuel
                        return !currentBlockInWorld.LastCodePart().Equals("lit") && bef != null &&
                               (currentStage < 3 || (currentStage >= 3 && bef.TotalFuel < BlockEntityBonfire.MaxFuel))
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

            return secondsIgniting > 3 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
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

            return secondsIgniting > 3 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }


        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            var hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            // Only proceed if holding firewood
            if (hotbarSlot?.Itemstack?.Collectible.Code.Path != "firewood")
                return base.OnBlockInteractStart(world, byPlayer, blockSel);

            BlockPos pos = blockSel.Position;
            // Get the actual block instance in the world at this position
            Block currentBlockInWorld = world.BlockAccessor.GetBlock(pos);
            string currentCodePart = currentBlockInWorld.LastCodePart(); // Use the actual block's code part

            // Get the BlockEntityBonfire
            BlockEntityBonfire? bef = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityBonfire;
            if (bef == null) return base.OnBlockInteractStart(world, byPlayer, blockSel); // Should not happen if it's a BlockBonfire

            // Determine current stage using the Stage property of the current block in world
            int currentStage = (currentBlockInWorld as BlockBonfire)?.Stage ?? 0;

            // Handle construction stages (Stage 0, 1, 2)
            if (currentStage < 3) // extinct, cold, construct1, construct2
            {
                // Determine the next state based on the current block's code part
                string nextStateCodePart;
                switch (currentCodePart)
                {
                    case "extinct":
                    case "cold":
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

                Block nextConstructionBlock = world.GetBlock(currentBlockInWorld.CodeWithVariant("burnstate", nextStateCodePart));
                world.BlockAccessor.ExchangeBlock(nextConstructionBlock.Id, pos);
                world.BlockAccessor.MarkBlockDirty(pos);
                if (nextConstructionBlock.Sounds != null) world.PlaySoundAt(nextConstructionBlock.Sounds.Place, pos.X, pos.Y, pos.Z, byPlayer);

                if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                {
                    hotbarSlot.TakeOut(1); // Consume 1 firewood for construction
                    hotbarSlot.MarkDirty();
                }
                return true;
            }
            // Handle fueling phase (Stage 3: construct3, Stage 4: cold)
            else if (currentStage >= 3) // This covers construct3 and cold states
            {
                if (bef.Refuel(1))
                {
                    // No visual change for the block itself here, it remains construct3 or cold
                    if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    {
                        hotbarSlot.TakeOut(1); // Consume 1 firewood for fuel
                        hotbarSlot.MarkDirty();
                    }
                    return true;
                }
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}