using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
                    "construct3" => "cold", // After construct3, it becomes a fully built 'cold' bonfire
                    _ => "cold"
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
                               (currentStage < 4 || (currentStage == 4 && bef.TotalFuel < bef.MaxFuel))
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

            // Handle construction stages
            // If it's cold, extinct, construct1, construct2, or construct3
            if (currentCodePart.Equals("cold") || currentCodePart.Equals("extinct") || currentCodePart.StartsWith("construct"))
            {
                // If it's construct3, the next interaction adds actual fuel and transitions to 'cold'
                if (currentCodePart.Equals("construct3"))
                {
                    if (bef.Refuel(1)) // Add 1 fuel to the BE
                    {
                        // Transition to the 'cold' state (fully built)
                        Block targetBlock = world.GetBlock(currentBlockInWorld.CodeWithVariant("burnstate", "cold"));
                        world.BlockAccessor.ExchangeBlock(targetBlock.Id, pos);
                        world.BlockAccessor.MarkBlockDirty(pos);
                        if (targetBlock.Sounds != null) world.PlaySoundAt(targetBlock.Sounds.Place, pos.X, pos.Y, pos.Z, byPlayer);

                        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                        {
                            hotbarSlot.TakeOut(1);
                            hotbarSlot.MarkDirty();
                        }
                        return true;
                    }
                }
                else // For cold, extinct, construct1, construct2: advance construction stage
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
                            // This case should ideally not be reached if the outer if condition is correct
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
            }
            // If it's already a fully built 'cold' bonfire (after construct3) and not burning, add fuel
            else if (currentCodePart.Equals("cold"))
            {
                if (bef.Refuel(1))
                {
                    if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    {
                        hotbarSlot.TakeOut(1);
                        hotbarSlot.MarkDirty();
                    }
                    return true;
                }
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}