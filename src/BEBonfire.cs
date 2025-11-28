using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Bonfires
{
    public class BlockEntityBonfire : BlockEntity, IHeatSource
    {
        public double BurningUntilTotalHours;
        public float BurnTimeHours = 16;
        private Block? _fireBlock;
        public string startedByPlayerUid = null!;
        private ILoadedSound? _ambientSound;
        private long _listener;

        private static readonly Cuboidf FireCuboid = new(-0.35f, 0, -0.35f, 1.35f, 2.8f, 1.35f);

        public bool Burning => Block.LastCodePart().Equals("lit");

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            _fireBlock = Api.World.GetBlock(new AssetLocation("fire"));
            if (_fireBlock == null)
            {
                // FIX: If the fire block is missing, log a warning and disable fire spread.
                Api.World.Logger.Warning("Bonfires mod could not find block with code 'fire'. Fire spreading from bonfires will be disabled.");
            }

            if (Burning)
            {
                InitSoundsAndTicking();
            }
        }

        private void InitSoundsAndTicking()
        {
            _listener = RegisterGameTickListener(OnceASecond, 1000);
            if (_ambientSound == null && Api.Side == EnumAppSide.Client)
            {
                _ambientSound = ((IClientWorldAccessor)Api.World).LoadSound(new SoundParams
                {
                    Location = new AssetLocation("bonfires-return:sounds/bonfire.ogg"),
                    ShouldLoop = true,
                    Position = Pos.ToVec3f().Add(0.5f, 0.25f, 0.5f),
                    DisposeOnFinish = false,
                    Volume = 2f
                });

                if (_ambientSound != null)
                {
                    _ambientSound.PlaybackPosition = _ambientSound.SoundLengthSeconds * (float)Api.World.Rand.NextDouble();
                    _ambientSound.Start();
                }
            }
        }

        private void OnceASecond(float dt)
        {
            if (Api is ICoreClientAPI)
            {
                if (!Burning)
                {
                    _ambientSound?.FadeOutAndStop(1);
                }
                return;
            }
            if (Burning)
            {
                Entity[] entities = Api.World.GetEntitiesAround(Pos.ToVec3d().Add(0.5, 0.5, 0.5), 3, 3, _ => true);
                Vec3d ownPos = Pos.ToVec3d();
                foreach (Entity entity in entities)
                {
                    if (!CollisionTester.AabbIntersect(entity.CollisionBox, entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z, FireCuboid, ownPos)) continue;

                    if (entity.Alive)
                    {
                        entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Block, SourceBlock = _fireBlock, SourcePos = ownPos, Type = EnumDamageType.Fire }, 2f);
                    }

                    if (Api.World.Rand.NextDouble() < 0.125)
                    {
                        entity.Ignite();
                    }
                }

                if (Api.World.BlockAccessor.GetBlock(Pos).LiquidCode == "water")
                {
                    KillFire();
                    return;
                }
                if (((ICoreServerAPI)Api).Server.Config.AllowFireSpread && 0.2 > Api.World.Rand.NextDouble())
                {
                    TrySpreadFireAllDirs();
                }
                if (Api.World.Calendar.TotalHours >= BurningUntilTotalHours)
                {
                    KillFire();
                    // See if we want to crack the blocks around us.
                    foreach (BlockFacing facing in BlockFacing.ALLFACES)
                    {
                        BlockPos npos = Pos.AddCopy(facing);
                        Block belowBlock = Api.World.BlockAccessor.GetBlock(npos);
                        AssetLocation? cracked = null;
                        if (belowBlock.FirstCodePart().Equals("ore"))
                        {
                            cracked = belowBlock.CodeWithPart("cracked_ore");
                            cracked.Domain = "bonfires-return";
                        }
                        else if (belowBlock.FirstCodePart().Equals("rock"))
                        {
                            cracked = belowBlock.CodeWithPart("cracked_rock");
                            cracked.Domain = "bonfires-return";
                        }
                        if (cracked != null && cracked.Valid)
                        {
                            Block crackedBlock = Api.World.GetBlock(cracked);
                            Api.World.BlockAccessor.ExchangeBlock(crackedBlock.Id, npos);
                        }
                    }
                }
            }
        }

        public void SetBlockState(string state)
        {
            AssetLocation loc = Block.CodeWithVariant("burnstate", state);
            Block block = Api.World.GetBlock(loc);
            if (block == null) return;

            Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
            this.Block = block;
        }

        public void Ignite(string playerUid)
        {
            startedByPlayerUid = playerUid;
            BurningUntilTotalHours = Api.World.Calendar.TotalHours + BurnTimeHours;
            SetBlockState("lit");
            MarkDirty(true);
            InitSoundsAndTicking();
        }

        public void KillFire()
        {
            SetBlockState("extinct");
            UnregisterGameTickListener(_listener);
        }

        public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        {
            return Burning ? 30 : 1;
        }

        private void TrySpreadFireAllDirs()
        {
            foreach (BlockFacing facing in BlockFacing.ALLFACES)
            {
                BlockPos npos = Pos.AddCopy(facing);
                TrySpreadTo(npos);
            }
            for (int up = 2; up <= 5; up++)
            {
                BlockPos npos = Pos.UpCopy(up);
                TrySpreadTo(npos);
            }
        }

        public bool TrySpreadTo(BlockPos pos)
        {
            // FIX: If the fire block asset is missing, don't attempt to spread fire.
            if (_fireBlock == null) return false;

            var block = Api.World.BlockAccessor.GetBlock(pos);
            if (block.Replaceable < 6000) return false;

            BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(pos);
            if (be?.GetBehavior<BEBehaviorBurning>() != null) return false;

            BlockPos? fuelPos = null;
            foreach (BlockFacing firefacing in BlockFacing.ALLFACES)
            {
                var npos = pos.AddCopy(firefacing);
                if (CanBurn(npos) && Api.World.BlockAccessor.GetBlockEntity(npos)?.GetBehavior<BEBehaviorBurning>() == null)
                {
                    fuelPos = npos;
                    break;
                }
            }
            if (fuelPos == null) return false;

            IPlayer? player = Api.World.PlayerByUid(startedByPlayerUid);
            if (player != null && Api.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted)
            {
                return false;
            }

            Api.World.BlockAccessor.SetBlock(_fireBlock.BlockId, pos);

            BlockEntity? befire = Api.World.BlockAccessor.GetBlockEntity(pos);
            if (befire != null)
            {
                befire.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(pos, fuelPos, startedByPlayerUid);
            }

            return true;
        }

        private bool CanBurn(BlockPos pos)
        {
            return
                OnCanBurn(pos)
                && Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.IsReinforced(pos) != true
            ;
        }

        private bool OnCanBurn(BlockPos pos)
        {
            Block block = Api.World.BlockAccessor.GetBlock(pos);
            return block.CombustibleProps is { BurnDuration: > 0 };
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            BurningUntilTotalHours = tree.GetDouble("BurningUntilTotalHours");
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetDouble("BurningUntilTotalHours", BurningUntilTotalHours);
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();

            if (_ambientSound != null)
            {
                _ambientSound.Stop();
                _ambientSound.Dispose();
                _ambientSound = null;
            }
        }
    }
}