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

        public int Stage
        {
            get
            {
                return LastCodePart() switch
                {
                    "construct1" => 1,
                    "construct2" => 2,
                    "construct3" => 3,
                    _ => 4
                };
            }
        }

        public string NextStageCodePart
        {
            get
            {
                return LastCodePart() switch
                {
                    "construct1" => "construct2",
                    "construct2" => "construct3",
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
                        var bf = coreApi.World.BlockAccessor.GetBlock(bs.Position);
                        return bf.LastCodePart().Equals("cold") ? wi.Itemstacks : null;
                    }
                },
                new WorldInteraction
                {
                    ActionLangCode = "bonfires:blockhelp-bonfire-fuel",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = fuelStacks.ToArray(),
                    GetMatchingStacks = (wi, bs, _) =>
                    {
                        var bf = coreApi.World.BlockAccessor.GetBlock(bs.Position);
                        return bf.LastCodePart().StartsWith("construct") ? wi.Itemstacks : null;
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
            if (Stage < 4 && hotbarSlot?.Itemstack?.Collectible.Code.Path == "firewood" && hotbarSlot.StackSize >= 8)
            {
                BlockPos pos = blockSel.Position;
                Block block = world.GetBlock(CodeWithParts(NextStageCodePart));
                world.BlockAccessor.ExchangeBlock(block.BlockId, pos);
                world.BlockAccessor.MarkBlockDirty(pos);
                if (block.Sounds != null) world.PlaySoundAt(block.Sounds.Place, pos.X, pos.Y, pos.Z, byPlayer);
                if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
                {
                    hotbarSlot.TakeOut(8);
                    hotbarSlot.MarkDirty();
                }
                return true;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}