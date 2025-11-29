using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Bonfires
{
    public class BlockEntityBonfire : BlockEntity, IHeatSource
    {
        // Constants for magic numbers
        private const int FIREWOOD_BURNTIME_MULTIPLIER = 4;
        private const int FALLBACK_FIREWOOD_BURNTIME = 24;
        private const int HEAT_STRENGTH_BURNING = 30;
        private const int HEAT_STRENGTH_EXTINCT = 1;
        private const float AMBIENT_SOUND_VOLUME = 2f;
        private const float FIRE_DAMAGE = 2f;
        private const double IGNITE_CHANCE = 0.125;
        private const double FIRE_SPREAD_CHANCE = 0.2;
        private const int ENTITY_DETECTION_RANGE = 3;
        private const int FIRE_SPREAD_UP_MIN = 2;
        private const int FIRE_SPREAD_UP_MAX = 5;

        private float _remainingBurnSeconds;
        private float _secondsPerFuelItem;

        private Block? _fireBlock;
        public string StartedByPlayerUid = null!;
        private ILoadedSound? _ambientSound;
        private long _listener;

        public const int MAX_FUEL = 32;

        private static readonly Cuboidf FireCuboid = new(-0.35f, 0, -0.35f, 1.35f, 2.8f, 1.35f);

        public bool Burning => Block.LastCodePart().Equals("lit");
        public int TotalFuel => _secondsPerFuelItem > 0 ? (int)Math.Ceiling(_remainingBurnSeconds / _secondsPerFuelItem) : 0;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            var firewood = Api.World.GetItem(new AssetLocation("firewood"));
            if (firewood?.CombustibleProps != null)
            {
                _secondsPerFuelItem = firewood.CombustibleProps.BurnDuration * FIREWOOD_BURNTIME_MULTIPLIER;
            }
            else
            {
                _secondsPerFuelItem = FALLBACK_FIREWOOD_BURNTIME * FIREWOOD_BURNTIME_MULTIPLIER;
            }

            _fireBlock = Api.World.GetBlock(new AssetLocation("fire"));
            if (_fireBlock == null)
            {
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
                    Volume = AMBIENT_SOUND_VOLUME
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
            if (Api.Side == EnumAppSide.Client)
            {
                if (!Burning)
                {
                    _ambientSound?.FadeOutAndStop(1);
                }
                return;
            }

            if (Burning)
            {
                int oldFuel = TotalFuel;
                _remainingBurnSeconds -= dt;

                if (oldFuel != TotalFuel)
                {
                    MarkDirty(true);
                }

                if (_remainingBurnSeconds <= 0)
                {
                    KillFire();
                    
                    foreach (BlockFacing facing in BlockFacing.ALLFACES)
                    {
                        BlockPos npos = Pos.AddCopy(facing);
                        Block? belowBlock = Api.World.BlockAccessor.GetBlock(npos);
                        if (belowBlock == null) continue;

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
                    return;
                }

                Entity[] entities = Api.World.GetEntitiesAround(Pos.ToVec3d().Add(0.5, 0.5, 0.5), ENTITY_DETECTION_RANGE, ENTITY_DETECTION_RANGE, _ => true);
                Vec3d ownPos = Pos.ToVec3d();
                foreach (Entity entity in entities)
                {
                    if (!CollisionTester.AabbIntersect(entity.CollisionBox, entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z, FireCuboid, ownPos)) continue;

                    if (entity.Alive)
                    {
                        entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Block, SourceBlock = _fireBlock, SourcePos = ownPos, Type = EnumDamageType.Fire }, FIRE_DAMAGE);
                    }

                    if (Api.World.Rand.NextDouble() < IGNITE_CHANCE)
                    {
                        entity.Ignite();
                    }
                }

                if (Api.World.BlockAccessor.GetBlock(Pos).LiquidCode == "water")
                {
                    KillFire();
                    return;
                }

                if (((ICoreServerAPI)Api).Server.Config.AllowFireSpread && FIRE_SPREAD_CHANCE > Api.World.Rand.NextDouble())
                {
                    TrySpreadFireAllDirs();
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
            if (TotalFuel <= 0) return;

            StartedByPlayerUid = playerUid;
            SetBlockState("lit");
            MarkDirty(true);
            InitSoundsAndTicking();
        }

        public void KillFire()
        {
            _remainingBurnSeconds = 0;
            UnregisterGameTickListener(_listener);

            // Crack ore/rock logic
            foreach (BlockFacing facing in BlockFacing.ALLFACES)
            {
                BlockPos npos = Pos.AddCopy(facing);
                Block? belowBlock = Api.World.BlockAccessor.GetBlock(npos);
                if (belowBlock == null) continue;

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
            
            SetBlockState("extinct"); // Ensure it goes to extinct state
        }

        public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        {
            return Burning ? HEAT_STRENGTH_BURNING : HEAT_STRENGTH_EXTINCT;
        }

        private void TrySpreadFireAllDirs()
        {
            foreach (BlockFacing facing in BlockFacing.ALLFACES)
            {
                BlockPos npos = Pos.AddCopy(facing);
                TrySpreadTo(npos);
            }
            for (int up = FIRE_SPREAD_UP_MIN; up <= FIRE_SPREAD_UP_MAX; up++)
            {
                BlockPos npos = Pos.UpCopy(up);
                TrySpreadTo(npos);
            }
        }

        public bool TrySpreadTo(BlockPos pos)
        {
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

            IPlayer? player = Api.World.PlayerByUid(StartedByPlayerUid);
            if (player != null && Api.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted)
            {
                return false;
            }

            Api.World.BlockAccessor.SetBlock(_fireBlock.BlockId, pos);

            BlockEntity? befire = Api.World.BlockAccessor.GetBlockEntity(pos);
            if (befire != null)
            {
                befire.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(pos, fuelPos, StartedByPlayerUid);
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
            _remainingBurnSeconds = tree.GetFloat("remainingBurnSeconds");
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("remainingBurnSeconds", _remainingBurnSeconds);
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

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            dsc.AppendLine(Lang.Get("bonfires-return:bonfire-fuel", TotalFuel, MAX_FUEL));
        }

        public bool Refuel(int amount)
        {
            if (TotalFuel >= MAX_FUEL) return false;

            _remainingBurnSeconds += amount * _secondsPerFuelItem;
            if (TotalFuel > MAX_FUEL)
            {
                _remainingBurnSeconds = MAX_FUEL * _secondsPerFuelItem;
            }

            MarkDirty(true);
            return true;
        }
    }
}